using System.Diagnostics;
using SessionKeeper.Services;
using Xunit.Abstractions;

namespace SessionKeeper.Tests;

/// <summary>
/// Reads the real ~/.claude data (read-only) and prints what the app would show.
/// Runs only when SK_LIVE=1:  $env:SK_LIVE=1; dotnet test --filter LiveSmoke --logger "console;verbosity=detailed"
/// </summary>
public sealed class LiveSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void LiveSmoke()
    {
        if (Environment.GetEnvironmentVariable("SK_LIVE") != "1") return;

        var sw = Stopwatch.StartNew();
        var running = new RunningScanner().Scan();
        var scanMs = sw.ElapsedMilliseconds;
        var store = new SessionStore();
        output.WriteLine($"Running sessions: {running.Count} (scan {scanMs} ms)");
        foreach (var r in running)
        {
            var info = r.SessionId != null ? store.GetSession(r.SessionId, r.Cwd) : null;
            output.WriteLine($"  pid {r.Pid,-6} {r.State,-9} {r.SessionId ?? "-",-36} {info?.ProjectDir ?? r.Cwd ?? "?"}");
            output.WriteLine($"      title: {info?.Title}   last: {Trim(info?.LastPrompt)}");
            if (r.Note != null) output.WriteLine("      note: " + r.Note);
        }

        sw.Restart();
        var recent = store.GetRecent(DateTimeOffset.Now.AddDays(-7));
        output.WriteLine($"\nRecent (7 days): {recent.Count} sessions, first load {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        store.GetRecent(DateTimeOffset.Now.AddDays(-7));
        output.WriteLine($"Second (cached) load {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        new RunningScanner().Scan();
        output.WriteLine($"Repeat scan {sw.ElapsedMilliseconds} ms");
        foreach (var s in recent.Take(15))
            output.WriteLine($"  {s.LastActive.ToLocalTime():MM-dd HH:mm} {s.SessionId[..8]} {s.ProjectDir,-40} {s.Title} | {Trim(s.LastPrompt)}");
        output.WriteLine("\nclaude: " + Launcher.FindClaude() + "\nwt: " + Launcher.FindWindowsTerminal());
    }

    private static string Trim(string? s) => s == null ? "" : s.Length > 70 ? s[..70] + "…" : s;
}
