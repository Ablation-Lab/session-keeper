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
        RenameMigration.Run();
        if (Cli.IsCliInvocation(args)) return Cli.Run(args);

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

        var app = new App { StartInTray = startInTray };
        app.InitializeComponent();
        return app.Run();
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
