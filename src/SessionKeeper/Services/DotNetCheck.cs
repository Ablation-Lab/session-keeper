using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SessionKeeper.Services;

/// <summary>The .NET runtime this process is running on, detected at runtime.</summary>
public static class DotNetRuntime
{
    public static Version Current => Environment.Version;

    public static string Display(Version v) => $".NET {v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}

/// <param name="Outdated">A newer patch exists for this .NET channel, or the channel is out of support.</param>
/// <param name="Detail">Tooltip text: the latest version and why.</param>
public sealed record RuntimeStatus(bool Outdated, string Detail);

/// <summary>
/// Compares the running .NET version with Microsoft's official release metadata (releases-index.json, then
/// the channel's releases.json for whether the newer patches carry security fixes). HTTPS only, Microsoft
/// hosts only, size-capped, short timeout, cached for 24 h. Read-only: never installs or updates anything.
/// Returns null when it can't tell (offline, blocked, unexpected data) so the footer stays grey.
/// </summary>
public sealed class DotNetReleaseCheck
{
    public static readonly string[] IndexUrls =
    {
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json",
    };
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "builds.dotnet.microsoft.com", "dotnetcli.blob.core.windows.net" };

    private const long MaxDownloadBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan SupportWarning = TimeSpan.FromDays(30);

    private readonly HttpMessageHandler? _handler;
    private readonly string _cacheDir;
    private readonly Func<DateTimeOffset> _now;

    public DotNetReleaseCheck(HttpMessageHandler? handler = null, string? cacheDir = null, Func<DateTimeOffset>? now = null)
    {
        _handler = handler;
        _cacheDir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionKeeper", "cache");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>What we keep from Microsoft's metadata for one channel (also the cache file format).</summary>
    public sealed class ChannelInfo
    {
        public string Channel { get; set; } = "";
        public DateTimeOffset FetchedAt { get; set; }
        public string? LatestRuntime { get; set; }
        public bool LatestSecurity { get; set; }
        public string? SupportPhase { get; set; }
        public string? EolDate { get; set; }
        /// <summary>Null when releases.json couldn't be read (the index alone is still usable).</summary>
        public List<ReleaseInfo>? Releases { get; set; }
    }

    public sealed record ReleaseInfo(string Runtime, string? Date, bool Security, int Cves);

    public async Task<RuntimeStatus?> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            var channel = $"{current.Major}.{current.Minor}";
            var info = ReadCache(channel);
            if (info == null)
            {
                info = await FetchAsync(channel, ct).ConfigureAwait(false);
                if (info != null) WriteCache(info);
            }
            return info == null ? null : Evaluate(current, info, _now());
        }
        catch
        {
            return null; // offline, timed out, blocked or unexpected data: stay grey
        }
    }

    // ------------------------------------------------------------------ evaluation (pure, unit-tested)

    public static RuntimeStatus? Evaluate(Version current, ChannelInfo info, DateTimeOffset now)
    {
        if (!TryVersion(info.LatestRuntime, out var latest)) return null;
        DateTimeOffset? eol = DateTimeOffset.TryParse(info.EolDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        var eolText = eol?.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var name = $".NET {info.Channel}";

        if (string.Equals(info.SupportPhase, "eol", StringComparison.OrdinalIgnoreCase) || (eol is { } end && now.Date >= end.Date))
            return new RuntimeStatus(true,
                $"{name} is out of support{(eolText != null ? " since " + eolText : "")}: Microsoft no longer releases security fixes for it. " +
                $"Latest {name} patch: {latest.ToString(3)}.");

        string supportNote = eol is { } soon && soon - now <= SupportWarning
            ? $" {name} support ends {eolText} (in {Math.Max(0, (int)Math.Ceiling((soon.Date - now.Date).TotalDays))} days)."
            : "";

        if (Compare(latest, current) > 0)
        {
            var newer = info.Releases?.Where(r => TryVersion(r.Runtime, out var v) && Compare(v, current) > 0).ToList();
            bool security = newer is { Count: > 0 } ? newer.Any(r => r.Security) : info.LatestSecurity;
            int cves = newer?.Sum(r => r.Cves) ?? 0;
            var why = security ? "security fixes" + (cves > 0 ? $" ({cves} CVE{(cves == 1 ? "" : "s")})" : "") : "bug fixes";
            var count = newer is { Count: > 1 } ? $" ({newer.Count} newer patches)" : "";
            return new RuntimeStatus(true, $".NET {latest.ToString(3)} available – {why}{count}.{supportNote}");
        }

        return new RuntimeStatus(false, $"Latest {name} patch.{supportNote}");
    }

    private static int Compare(Version a, Version b) =>
        new Version(a.Major, a.Minor, Math.Max(0, a.Build)).CompareTo(new Version(b.Major, b.Minor, Math.Max(0, b.Build)));

    private static bool TryVersion(string? s, out Version v)
    {
        v = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var core = s.Split('-', '+')[0]; // ignore any preview/build suffix
        return Version.TryParse(core, out v!) && v.Build >= 0;
    }

    // ------------------------------------------------------------------ fetching

    private async Task<ChannelInfo?> FetchAsync(string channel, CancellationToken ct)
    {
        using var http = _handler != null ? new HttpClient(_handler, disposeHandler: false) : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(6);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SessionKeeper/" + BuildInfo.Current.Version);

        JsonElement? entry = null;
        foreach (var url in IndexUrls)
        {
            var index = await GetJsonAsync(http, url, ct).ConfigureAwait(false);
            if (index == null) continue;
            using var doc = index;
            if (doc.RootElement.TryGetProperty("releases-index", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var r in list.EnumerateArray())
                    if (Str(r, "channel-version") == channel) { entry = r.Clone(); break; }
            if (entry != null) break;
        }
        if (entry is not { } ch) return null;

        var info = new ChannelInfo
        {
            Channel = channel,
            FetchedAt = _now(),
            LatestRuntime = Str(ch, "latest-runtime"),
            LatestSecurity = ch.TryGetProperty("security", out var sec) && sec.ValueKind == JsonValueKind.True,
            SupportPhase = Str(ch, "support-phase"),
            EolDate = Str(ch, "eol-date"),
        };

        // The channel's releases.json says which newer patches carry security fixes. Optional: without it the
        // index's latest-release security flag is used.
        if (Str(ch, "releases.json") is { } relUrl && IsAllowed(relUrl))
        {
            using var rel = await GetJsonAsync(http, relUrl, ct).ConfigureAwait(false);
            if (rel != null && rel.RootElement.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
            {
                info.Releases = new List<ReleaseInfo>();
                foreach (var r in releases.EnumerateArray())
                {
                    var runtime = r.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object ? Str(rt, "version") : null;
                    if (runtime == null) continue;
                    int cves = r.TryGetProperty("cve-list", out var cl) && cl.ValueKind == JsonValueKind.Array ? cl.GetArrayLength() : 0;
                    info.Releases.Add(new ReleaseInfo(runtime, Str(r, "release-date"),
                        r.TryGetProperty("security", out var s) && s.ValueKind == JsonValueKind.True, cves));
                    if (info.Releases.Count >= 60) break; // newest first; older patches don't matter
                }
            }
        }
        return info;
    }

    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host);

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!IsAllowed(url)) return null;
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Redirects are followed by HttpClient; make sure we still ended up on HTTPS at a Microsoft host.
            var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            LaunchGuard.LogEvent("FETCH", $"url={url}  status={(int)resp.StatusCode}" + (final != url ? $"  final={final}" : ""));
            if (!resp.IsSuccessStatusCode || !IsAllowed(final)) return null;
            if (resp.Content.Headers.ContentLength is > MaxDownloadBytes) return null;

            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaxDownloadBytes) return null;
                buffer.Write(chunk, 0, n);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LaunchGuard.LogEvent("FETCH", $"url={url}  failed={ex.GetType().Name}");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        try { return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        catch (InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------ 24 h cache

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string CachePath(string channel) => Path.Combine(_cacheDir, $"dotnet-{channel}.json");

    private ChannelInfo? ReadCache(string channel)
    {
        try
        {
            var path = CachePath(channel);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            var info = JsonSerializer.Deserialize<ChannelInfo>(File.ReadAllText(path, Encoding.UTF8), CacheJson);
            if (info == null || info.Channel != channel) return null;
            var age = _now() - info.FetchedAt;
            return age >= TimeSpan.Zero && age < CacheLifetime ? info : null; // stale or from the future: refetch
        }
        catch
        {
            return null;
        }
    }

    private void WriteCache(ChannelInfo info)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = CachePath(info.Channel);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(info, CacheJson), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A cache we can't write just means fetching again next start.
        }
    }
}
