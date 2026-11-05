using System.Runtime.InteropServices;
using Stowdeck.Services;

namespace Stowdeck;

/// <summary>
/// Entry point. Command-line options (--list, --snapshot, ...) run headless and exit with a status code.
/// Otherwise the UI starts as a single instance: launching it again brings the existing window forward
/// (including from the tray) instead of opening a second copy.
/// </summary>
public static class Program
{
    internal const string StartupFlag = "--startup";
    private const string InstanceMutexName = @"Local\Stowdeck.Instance";
    internal const string ShowEventName = @"Local\Stowdeck.Show";
    /// <summary>Set by install.ps1 (after asking the user) to make a running instance exit cleanly.</summary>
    internal const string ExitEventName = @"Local\Stowdeck.Exit";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    public static int Main(string[] args)
    {
        // The app used to be called SessionKeeper: its settings and snapshots move over on the first run, whether
        // that's the window, the command line (the Claude Code plugin) or the MCP connector.
        var moved = RenameMigration.Run();
        if (Cli.IsCliInvocation(args))
        {
            if (moved.Outcome is RenameMigration.Outcome.Deferred or RenameMigration.Outcome.Failed)
                Cli.StartupNotice = MoveNotice(moved) + " " + MoveRetry;
            return Cli.Run(args);
        }

        bool startInTray = args.Any(a => string.Equals(a, StartupFlag, StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Ask the running instance to show itself, and let it take the foreground.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                using (show)
                {
                    // Let only the running Stowdeck take the foreground (not ASFW_ANY, which would let any process).
                    if (FindRunningInstance() is int firstPid) AllowSetForegroundWindow(firstPid);
                    show.Set();
                }
            }
            return 0;
        }

        // At sign-in (--startup) nothing pops up: the move is just tried again on the next start.
        if (!startInTray) ReportMove(moved);
        var app = new App { StartInTray = startInTray };
        app.InitializeComponent();
        return app.Run();
    }

    private const string MoveRetry = "Nothing was lost: they move over the next time Stowdeck starts.";

    private static string MoveNotice(RenameMigration.Result moved) => moved.Outcome == RenameMigration.Outcome.Deferred
        ? "SessionKeeper, this app's old name, is still running, so your settings, session names and snapshots haven't moved over to Stowdeck yet."
        : "Your settings, session names and snapshots couldn't be moved over from SessionKeeper, this app's old name (" + moved.Message + ").";

    /// <summary>
    /// Before the window opens: if the move from the old name was put off because SessionKeeper is still running, offer
    /// to close it and move now; if the move failed, say so. Either way it's tried again on the next start.
    /// </summary>
    private static void ReportMove(RenameMigration.Result moved)
    {
        if (moved.Outcome == RenameMigration.Outcome.Deferred)
        {
            var answer = System.Windows.MessageBox.Show(
                MoveNotice(moved) + "\n\nClose SessionKeeper now and move them?\n\nNo starts Stowdeck without them for now. " + MoveRetry,
                "Stowdeck", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
            if (!RenameMigration.CloseOldApp(TimeSpan.FromSeconds(10)))
            {
                System.Windows.MessageBox.Show(
                    "SessionKeeper didn't close. Close it yourself (right-click its tray icon > Exit), and if Claude Code or Claude Desktop " +
                    "still has its plugin or connector, remove those, then restart Stowdeck. " + MoveRetry,
                    "Stowdeck", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            moved = RenameMigration.Run();
        }
        if (moved.Outcome is RenameMigration.Outcome.Failed or RenameMigration.Outcome.Deferred)
            System.Windows.MessageBox.Show(MoveNotice(moved) + "\n\n" + MoveRetry, "Stowdeck",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>PID of the other Stowdeck process running from the same executable, if any.</summary>
    private static int? FindRunningInstance()
    {
        var me = Environment.ProcessId;
        var exe = Environment.ProcessPath;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Stowdeck"))
        {
            using (p)
            {
                if (p.Id == me) continue;
                if (string.Equals(Native.QueryProcess(p.Id).ImagePath, exe, StringComparison.OrdinalIgnoreCase)) return p.Id;
            }
        }
        return null;
    }
}
