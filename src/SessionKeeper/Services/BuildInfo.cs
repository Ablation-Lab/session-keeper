using System.Globalization;
using System.Reflection;

namespace SessionKeeper.Services;

/// <summary>Version and build details embedded at build time (see the EmbedBuildInfo target in the project file).</summary>
public sealed record BuildInfo(string Version, string? Commit, DateTimeOffset? BuildDate, string? SourceRepo, bool SourceDirty)
{
    public static BuildInfo Current { get; } = FromAssembly(typeof(BuildInfo).Assembly);

    public string? ShortCommit => Commit is { Length: >= 7 } c ? c[..7] : Commit;

    /// <summary>e.g. "v1.0.0 · build a1b2c3d · 3 Nov 2026" (parts that weren't embedded are left out).</summary>
    public string FooterText
    {
        get
        {
            var parts = new List<string> { "v" + Version };
            if (ShortCommit != null) parts.Add("build " + ShortCommit + (SourceDirty ? "+" : ""));
            if (BuildDate is { } d) parts.Add(d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture));
            return string.Join(" · ", parts);
        }
    }

    public static BuildInfo FromAssembly(Assembly asm)
    {
        var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return FromValues(informational ?? asm.GetName().Version?.ToString(3), meta);
    }

    internal static BuildInfo FromValues(string? informationalVersion, IReadOnlyDictionary<string, string?> meta)
    {
        var version = (informationalVersion ?? "0.0.0").Split('+')[0].Trim();
        string? Get(string key) => meta.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var commit = Get("CommitHash");
        if (commit != null && !IsHexCommit(commit)) commit = null; // never trust anything but a hex hash
        DateTimeOffset? date = DateTimeOffset.TryParse(Get("BuildDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
        return new BuildInfo(version, commit, date, Get("SourceRepo"), string.Equals(Get("SourceDirty"), "true", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHexCommit(string s) => s.Length is >= 7 and <= 64 && s.All(Uri.IsHexDigit);
}
