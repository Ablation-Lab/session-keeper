using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SessionKeeper;

public partial class App : Application
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionKeeper");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
        base.OnStartup(e);
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
