using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Stowdeck.Services;

/// <summary>The .NET runtime this process is running on, detected at runtime.</summary>
public static class DotNetRuntime
{
    public static Version Current => Environment.Version;

    public static string Display(Version v) => $".NET {v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    private static string? CoreLibLocation
    {
        get
        {
            try { return typeof(object).Assembly.Location; }
            catch { return null; }
        }
    }

    /// <summary>True when the runtime ships inside the app's own folder (self-contained or single-file).</summary>
    public static bool IsSelfContained => IsSelfContainedLayout(CoreLibLocation, AppContext.BaseDirectory);

    /// <summary>Folder the running runtime was loaded from (e.g. ...\dotnet\shared\Microsoft.NETCore.App\8.0.33).</summary>
    public static string? RuntimeFolder => string.IsNullOrEmpty(CoreLibLocation) ? null : Path.GetDirectoryName(CoreLibLocation);

    internal static bool IsSelfContainedLayout(string? coreLibLocation, string appBaseDir)
    {
        if (string.IsNullOrEmpty(coreLibLocation)) return true; // single-file bundle: the runtime is inside the exe
        var dir = Path.GetDirectoryName(coreLibLocation) ?? "";
        return string.Equals(Norm(dir), Norm(appBaseDir), StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string p) => p.TrimEnd('\\', '/');
}

/// <summary>.NET runtimes installed on this PC, read from the dotnet install folders (no dotnet.exe is run).</summary>
public sealed record InstalledRuntimes(IReadOnlyList<string> NetCore, IReadOnlyList<string> WindowsDesktop)
{
    public static InstalledRuntimes Find() => Find(CandidateRoots());

    public static InstalledRuntimes Find(IEnumerable<string> roots)
    {
        var core = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desktop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddVersions(Path.Combine(root, "shared", "Microsoft.NETCore.App"), core);
            AddVersions(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App"), desktop);
        }
        return new InstalledRuntimes(Sorted(core), Sorted(desktop));
    }

    /// <summary>Where dotnet installs live: DOTNET_ROOT, the registered install location, Program Files, and the running runtime's root.</summary>
    public static IEnumerable<string> CandidateRoots()
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            try { if (!string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)) roots.Add(Path.GetFullPath(p)); }
            catch { }
        }
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        try
        {
            // The installer records its location in the 32-bit registry view (read-only access).
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var key = hklm.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            Add(key?.GetValue("InstallLocation") as string);
        }
        catch { }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        // ...\dotnet\shared\Microsoft.NETCore.App\<version>  ->  ...\dotnet
        if (DotNetRuntime.RuntimeFolder is { } rf && !DotNetRuntime.IsSelfContained)
            Add(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(rf))));
        return roots;
    }

    private static void AddVersions(string dir, HashSet<string> into)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (char.IsDigit(name.FirstOrDefault()) && Version.TryParse(name.Split('-')[0], out _)) into.Add(name);
            }
        }
        catch { }
    }

    private static List<string> Sorted(IEnumerable<string> versions) => versions
        .OrderBy(v => Version.TryParse(v.Split('-')[0], out var p) ? p : new Version(0, 0))
        .ThenBy(v => v.Contains('-') ? 0 : 1) // previews before the release of the same number
        .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <param name="Outdated">A newer patch exists for this .NET channel, or the channel is out of support.</param>
/// <param name="Detail">Summary: the latest version and why.</param>
/// <param name="Latest">Latest patch of the channel, e.g. "8.0.33".</param>
/// <param name="Support">Support status, e.g. "maintenance, ends 10 Nov 2026 (in 7 days)".</param>
public sealed record RuntimeStatus(bool Outdated, string Detail, string? Latest = null, string? Support = null);

/// <summary>Builds the runtime part's tooltip (pure, unit-tested).</summary>
public static class RuntimeTooltip
{
    public static string Build(Version running, bool selfContained, InstalledRuntimes? installed, RuntimeStatus? status, string channel)
    {
        var lines = new List<string>
        {
            $"Running on: {DotNetRuntime.Display(running)} " + (selfContained ? "(self-contained – bundled with the app)" : "(framework-dependent)"),
        };
        if (installed != null)
        {
            lines.Add("Installed on this PC: " + (installed.NetCore.Count > 0 ? ".NET " + string.Join(", ", installed.NetCore) : "no shared .NET runtimes found"));
            if (installed.WindowsDesktop.Count > 0) lines.Add("    Windows Desktop runtime: " + string.Join(", ", installed.WindowsDesktop));
        }
        if (status?.Latest != null) lines.Add($"Latest .NET {channel} patch: {status.Latest}");
        if (status?.Support != null) lines.Add("Support: " + status.Support);
        if (status is { Outdated: true }) lines.Add(status.Detail);
        if (status is { Outdated: true } && selfContained)
            lines.Add("This copy is self-contained: updating .NET means rebuilding Stowdeck, not installing a new runtime.");
        return string.Join("\n", lines);
    }
}

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
        _cacheDir = cacheDir ?? Path.Combine(AppPaths.DataDir, "cache");
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

        var latestText = latest.ToString(3);
        if (string.Equals(info.SupportPhase, "eol", StringComparison.OrdinalIgnoreCase) || (eol is { } end && now.Date >= end.Date))
            return new RuntimeStatus(true,
                $"{name} is out of support{(eolText != null ? " since " + eolText : "")}: Microsoft no longer releases security fixes for it. " +
                $"Latest {name} patch: {latestText}.",
                latestText, "out of support" + (eolText != null ? " since " + eolText : ""));

        int? daysLeft = eol is { } e2 ? Math.Max(0, (int)Math.Ceiling((e2.Date - now.Date).TotalDays)) : null;
        bool endingSoon = eol is { } soon && soon - now <= SupportWarning;
        string supportNote = endingSoon ? $" {name} support ends {eolText} (in {daysLeft} days)." : "";
        string support = (string.IsNullOrEmpty(info.SupportPhase) ? "supported" : info.SupportPhase) +
                         (eolText != null ? $", ends {eolText}" + (endingSoon ? $" (in {daysLeft} days)" : "") : "");

        if (Compare(latest, current) > 0)
        {
            var newer = info.Releases?.Where(r => TryVersion(r.Runtime, out var v) && Compare(v, current) > 0).ToList();
            bool security = newer is { Count: > 0 } ? newer.Any(r => r.Security) : info.LatestSecurity;
            int cves = newer?.Sum(r => r.Cves) ?? 0;
            var why = security ? "security fixes" + (cves > 0 ? $" ({cves} CVE{(cves == 1 ? "" : "s")})" : "") : "bug fixes";
            var count = newer is { Count: > 1 } ? $" ({newer.Count} newer patches)" : "";
            return new RuntimeStatus(true, $".NET {latestText} available – {why}{count}.{supportNote}", latestText, support);
        }

        return new RuntimeStatus(false, $"Latest {name} patch.{supportNote}", latestText, support);
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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Stowdeck/" + BuildInfo.Current.Version);

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
