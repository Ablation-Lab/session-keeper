using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SessionKeeper;

public partial class App : Application
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionKeeper");

    /// <summary>Started by Windows at sign-in (--startup): stay in the tray until opened.</summary>
    public bool StartInTray { get; init; }

    private System.Windows.Forms.NotifyIcon? _tray;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;
    private volatile bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
        base.OnStartup(e);
        Services.LaunchGuard.LogEvent("APP", $"started mode={(StartInTray ? "tray (--startup)" : "window")} exe={Environment.ProcessPath}");

        var window = new MainWindow();
        MainWindow = window;
        StartShowListener();

        if (StartInTray && TryCreateTrayIcon()) return; // window stays hidden: no scanning until opened
        if (StartInTray) window.WindowState = WindowState.Minimized; // tray unavailable: start minimised
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        Services.LaunchGuard.LogEvent("APP", "exited");
        _showEvent?.Set(); // release the listener thread
        RemoveTrayIcon();
        base.OnExit(e);
    }

    /// <summary>Brings the main window up, from the tray, minimised or behind other windows.</summary>
    public void ShowMainWindow()
    {
        if (MainWindow is not { } w) return;
        RemoveTrayIcon();
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        // Nudge to the front even if Windows' focus rules held activation back.
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    /// <summary>
    /// Listens for two signals from other processes in this session: a second launch asking this instance to
    /// show itself, and the installer asking it to exit cleanly (so it can replace the files). Exiting this
    /// way runs the normal shutdown: the tray icon is removed and the exit is logged.
    /// </summary>
    private void StartShowListener()
    {
        EventWaitHandle exitEvent;
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        }
        catch (Exception ex)
        {
            Log(ex);
            return;
        }
        var show = _showEvent;
        _showListener = new Thread(() =>
        {
            var handles = new WaitHandle[] { show, exitEvent };
            while (!_exiting)
            {
                int which = WaitHandle.WaitAny(handles);
                if (_exiting) break;
                if (which == 0) Dispatcher.BeginInvoke(ShowMainWindow);
                else
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        Services.LaunchGuard.LogEvent("APP", "exit requested by the installer");
                        Shutdown();
                    });
                    break;
                }
            }
            exitEvent.Dispose();
        })
        { IsBackground = true, Name = "SessionKeeper signal listener" };
        _showListener.Start();
    }

    private bool TryCreateTrayIcon()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open SessionKeeper", null, (_, _) => ShowMainWindow());
            menu.Items.Add("Exit", null, (_, _) => Shutdown());
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "SessionKeeper",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Left) ShowMainWindow();
            };
            return true;
        }
        catch (Exception ex)
        {
            Log(ex);
            RemoveTrayIcon();
            return false;
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var info = GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        if (info?.Stream is { } s)
            using (s)
                return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
        return System.Drawing.SystemIcons.Application;
    }

    private void RemoveTrayIcon()
    {
        if (_tray == null) return;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _tray = null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to error.log in " + DataDir,
            "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var path = Path.Combine(DataDir, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
