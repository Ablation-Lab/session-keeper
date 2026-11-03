using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SessionKeeper.Models;

namespace SessionKeeper.Services;

/// <summary>
/// Reads Claude Code session transcripts (~/.claude/projects/&lt;encoded-dir&gt;/&lt;sessionId&gt;.jsonl).
/// Files are append-only, so each file is parsed incrementally from the last offset and cached.
/// Files are opened read-only with full sharing so Claude Code is never blocked.
/// </summary>
public sealed class SessionStore
{
    private const int MaxLineBytes = 4 * 1024 * 1024; // larger lines (image blobs etc.) are skipped
    private const long FastPathThreshold = 8L * 1024 * 1024;
    private const long HeadBytes = 256 * 1024;
    private const long TailBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, ParseState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private ArrayBufferWriter<byte> _carry = new(); // reused for lines spanning read buffers (guarded by _gate)

    public string ProjectsRoot { get; }

    public SessionStore(string? projectsRoot = null)
    {
        ProjectsRoot = projectsRoot ?? Path.Combine(ClaudePaths.ClaudeDir, "projects");
    }

    /// <summary>Claude Code's folder-name encoding: every non-alphanumeric character becomes '-'.</summary>
    public static string EncodeProjectDir(string path) => Regex.Replace(path, "[^a-zA-Z0-9]", "-");

    public static bool IsValidSessionId(string? id) =>
        id != null && Guid.TryParseExact(id, "D", out _);

    /// <summary>Finds the transcript for a session id, trying the folder implied by <paramref name="cwdHint"/> first.</summary>
    public string? FindFile(string sessionId, string? cwdHint = null)
    {
        if (!IsValidSessionId(sessionId) || !Directory.Exists(ProjectsRoot)) return null;
        var name = sessionId + ".jsonl";
        if (!string.IsNullOrEmpty(cwdHint))
        {
            var guess = Path.Combine(ProjectsRoot, EncodeProjectDir(cwdHint), name);
            if (File.Exists(guess)) return guess;
        }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public SessionInfo? GetSession(string sessionId, string? cwdHint = null)
    {
        var file = FindFile(sessionId, cwdHint);
        return file == null ? null : GetSessionFromFile(file);
    }

    public SessionInfo? GetSessionFromFile(string file) => GetSessionFromFile(file, null);

    /// <summary>
    /// <paramref name="known"/> is the file's FileInfo when the caller already has it from a directory
    /// enumeration, which saves a second stat per transcript on every refresh.
    /// </summary>
    private SessionInfo? GetSessionFromFile(string file, FileInfo? known)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(file, out var st))
            {
                st = new ParseState(file);
                _cache[file] = st;
            }
            st.Update(_carry, known);
            if (_carry.Capacity > 1024 * 1024) _carry = new(); // don't pin a multi-MB buffer all day
            if (st.Missing) { _cache.Remove(file); return null; } // deleted: don't keep its parse state
            return st.ToInfo();
        }
    }

    /// <summary>Number of transcripts with cached parse state (for tests).</summary>
    internal int CachedCount { get { lock (_gate) return _cache.Count; } }

    /// <summary>Sessions with activity since <paramref name="sinceUtc"/>, newest first.</summary>
    public List<SessionInfo> GetRecent(DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        var result = new List<SessionInfo>();
        if (!Directory.Exists(ProjectsRoot)) return result;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(ProjectsRoot).ToList(); }
        catch (Exception) { return result; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception) { continue; }

            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(fi.FullName);
                // mtime >= last record time, so this pre-filter never drops a qualifying session.
                if (fi.LastWriteTimeUtc < sinceUtc.UtcDateTime) continue;
                if (!IsValidSessionId(Path.GetFileNameWithoutExtension(fi.Name))) continue;
                var info = GetSessionFromFile(fi.FullName, fi);
                if (info != null && info.LastActive >= sinceUtc) result.Add(info);
            }
        }
        EvictDeleted(seen);
        result.Sort((a, b) => b.LastActive.CompareTo(a.LastActive));
        return result;
    }

    /// <summary>Drops cached parse state for transcripts that have been deleted since they were read.</summary>
    private void EvictDeleted(HashSet<string> seen)
    {
        lock (_gate)
        {
            // Not seen in this listing (or its folder couldn't be listed) and really gone: forget it.
            foreach (var path in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
                if (!File.Exists(path)) _cache.Remove(path);
        }
    }

    // ------------------------------------------------------------------ parsing

    private sealed class ParseState
    {
        private readonly string _path;
        private long _offset;
        private DateTime _lastWriteUtc;
        private bool _missing;

        private readonly List<string> _cwds = new();
        private string? _firstPrompt, _lastPrompt, _aiTitle, _customTitle, _agentName, _continuedIn;
        private DateTimeOffset? _firstTs, _lastTs;
        private bool _hasConversation;

        public ParseState(string path) => _path = path;

        private void Reset()
        {
            _offset = 0;
            _cwds.Clear();
            _firstPrompt = _lastPrompt = _aiTitle = _customTitle = _agentName = _continuedIn = null;
            _firstTs = _lastTs = null;
            _hasConversation = false;
        }

        public bool Missing => _missing;

        public void Update(ArrayBufferWriter<byte> carry, FileInfo? known = null)
        {
            FileInfo fi;
            try
            {
                fi = known ?? new FileInfo(_path);
                if (!fi.Exists) { _missing = true; return; }
            }
            catch (Exception) { _missing = true; return; }

            _missing = false;
            if (fi.Length == _offset) { _lastWriteUtc = fi.LastWriteTimeUtc; return; }

            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                long len = fs.Length;

                // Truncated, or rewritten to a larger size (our offset no longer sits on a line boundary).
                if (len < _offset || (_offset > 0 && !EndsLineAt(fs, _offset))) Reset();

                if (_offset == 0 && len > FastPathThreshold)
                {
                    // First look at a big transcript: the head has the project cwd and first prompt; the tail
                    // has the latest activity, last-prompt and ai-title (Claude re-emits those constantly).
                    // Everything appended later is still read in full.
                    ReadLines(fs, 0, HeadBytes, carry, skipPartialFirst: false);
                    _offset = ReadLines(fs, len - TailBytes, len, carry, skipPartialFirst: true);
                }
                else
                {
                    _offset = ReadLines(fs, _offset, len, carry, skipPartialFirst: false);
                }
                _lastWriteUtc = fi.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                // Locked, mid-rotation or unreadable: try again next refresh.
            }
        }

        private static bool EndsLineAt(FileStream fs, long offset)
        {
            fs.Seek(offset - 1, SeekOrigin.Begin);
            return fs.ReadByte() == '\n';
        }

        /// <summary>
        /// Parses complete lines in [start, end). Returns the position just after the last complete line
        /// (a partial trailing line is re-read next time, since Claude may be mid-write).
        /// </summary>
        private long ReadLines(FileStream fs, long start, long end, ArrayBufferWriter<byte> carry, bool skipPartialFirst)
        {
            var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            carry.Clear();
            bool skipping = skipPartialFirst; // inside an oversized line, or the partial line we seeked into
            long readPos = start;             // file position of buf[0]
            long consumed = start;            // position just after the last complete line
            fs.Seek(start, SeekOrigin.Begin);
            try
            {
                int n;
                while (readPos < end && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, end - readPos))) > 0)
                {
                    int i = 0;
                    while (i < n)
                    {
                        int nl = buf.AsSpan(i, n - i).IndexOf((byte)'\n');
                        if (nl < 0)
                        {
                            if (!skipping)
                            {
                                if (carry.WrittenCount + (n - i) > MaxLineBytes) { skipping = true; carry.Clear(); }
                                else carry.Write(buf.AsSpan(i, n - i));
                            }
                            break;
                        }

                        var piece = buf.AsSpan(i, nl);
                        if (!skipping)
                        {
                            if (carry.WrittenCount == 0) ProcessLine(piece);
                            else if (carry.WrittenCount + piece.Length <= MaxLineBytes)
                            {
                                carry.Write(piece);
                                ProcessLine(carry.WrittenSpan);
                            }
                        }
                        carry.Clear();
                        skipping = false;
                        consumed = readPos + i + nl + 1;
                        i += nl + 1;
                    }
                    readPos += n;
                }
                // An oversized partial line is skipped past; its tail will fail to parse and be ignored.
                return skipping ? readPos : consumed;
            }
            finally
            {
                carry.Clear();
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private static readonly byte[][] SkipPrefixes =
        {
            "{\"type\":\"api-request"u8.ToArray(),
            "{\"type\":\"file-history"u8.ToArray(),
            "{\"type\":\"tools-used"u8.ToArray(),
            "{\"type\":\"cost-state"u8.ToArray(),
        };

        private void ProcessLine(ReadOnlySpan<byte> line)
        {
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (line.Length < 2) return;
            foreach (var p in SkipPrefixes)
                if (line.StartsWith(p)) return;

            try
            {
                var reader = new Utf8JsonReader(line);
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var type = Str(root, "type");

                if (Str(root, "cwd") is { Length: > 0 } cwd && _cwds.Count < 64 &&
                    !_cwds.Contains(cwd, StringComparer.OrdinalIgnoreCase))
                    _cwds.Add(cwd);

                switch (type)
                {
                    case "user":
                    case "assistant":
                    case "system":
                        if (type != "system") _hasConversation = true;
                        if (Str(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t))
                        {
                            _firstTs ??= t;
                            if (_lastTs == null || t > _lastTs) _lastTs = t;
                        }
                        if (type == "user" && ExtractUserText(root) is { } text)
                        {
                            _firstPrompt ??= text;
                            _lastPrompt = text;
                        }
                        break;
                    case "last-prompt":
                        if (Clean(Str(root, "lastPrompt")) is { } lp) _lastPrompt = lp;
                        break;
                    case "ai-title":
                        _aiTitle = Clean(Str(root, "aiTitle")) ?? _aiTitle;
                        break;
                    case "custom-title":
                        _customTitle = Clean(Str(root, "customTitle")) ?? _customTitle;
                        break;
                    case "agent-name":
                        _agentName = Clean(Str(root, "agentName")) ?? _agentName;
                        break;
                    case "continued-in":
                        _continuedIn = Str(root, "continuedInSessionId") ?? _continuedIn;
                        break;
                }
            }
            catch (Exception)
            {
                // Malformed or half-written line, or a string cut mid-surrogate (GetString throws
                // InvalidOperationException): ignore this line, never stall the whole file.
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool True(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>The human-typed text of a user record, or null for tool results, meta and command noise.</summary>
        internal static string? ExtractUserText(JsonElement root)
        {
            if (True(root, "isMeta") || True(root, "isSidechain") || True(root, "isCompactSummary") ||
                True(root, "isVisibleInTranscriptOnly"))
                return null;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("content", out var content)) return null;

            string? text = null;
            if (content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    var bt = Str(block, "type");
                    if (bt == "tool_result") return null;
                    if (bt == "text" && Str(block, "text") is { } bText) { text = bText; break; }
                }
            }
            if (text == null) return null;
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('<') || trimmed.StartsWith("[Request interrupted", StringComparison.Ordinal) ||
                trimmed.StartsWith("Caveat:", StringComparison.Ordinal))
                return null;
            return Clean(text);
        }

        internal static string? Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new StringBuilder(Math.Min(s.Length, 400));
            bool space = false;
            foreach (var ch in s)
            {
                if (sb.Length >= 400) break;
                if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
                if (ch == '�') continue; // truncated multi-byte char in last-prompt records
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        public SessionInfo? ToInfo()
        {
            if (_missing || !_hasConversation) return null;
            var id = Path.GetFileNameWithoutExtension(_path);
            var dirName = Path.GetFileName(Path.GetDirectoryName(_path)) ?? "";
            // cwd changes mid-session when Claude cd's; the project dir is the cwd that encodes to the folder name.
            var project = _cwds.FirstOrDefault(c => string.Equals(EncodeProjectDir(c), dirName, StringComparison.OrdinalIgnoreCase))
                          ?? _cwds.FirstOrDefault();
            return new SessionInfo
            {
                SessionId = id,
                FilePath = _path,
                ProjectDir = project,
                Title = _customTitle ?? _agentName ?? _aiTitle,
                FirstPrompt = _firstPrompt,
                LastPrompt = _lastPrompt ?? _firstPrompt,
                LastActive = _lastTs ?? new DateTimeOffset(_lastWriteUtc, TimeSpan.Zero),
                Started = _firstTs,
                ContinuedIn = _continuedIn,
            };
        }
    }
}

internal static class ClaudePaths
{
    /// <summary>CLAUDE_CONFIG_DIR when it is set and exists (forwarded to launched sessions), else null.</summary>
    public static string? CustomConfigDir
    {
        get
        {
            var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                return !string.IsNullOrWhiteSpace(cfg) && Path.IsPathFullyQualified(cfg) && Directory.Exists(cfg)
                    ? Path.GetFullPath(cfg) : null;
            }
            catch { return null; }
        }
    }

    public static string ClaudeDir =>
        CustomConfigDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}
