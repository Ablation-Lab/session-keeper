using System.Windows;
using SessionKeeper.Services;

namespace SessionKeeper.ViewModels;

/// <summary>A project in the Remotion Studio dialog. Its running status fills in once the background check is done.</summary>
public sealed class RemotionRow : Observable
{
    private StudioInstance? _running;

    public RemotionRow(RemotionProject project) => Project = project;

    public RemotionProject Project { get; }
    public string Name => Project.Name;
    public string Dir => Project.Dir;

    /// <summary>What Start runs, in words: the dev script (shown, as it's what really runs) or Remotion's own command.</summary>
    public string CommandText => Project.DevRunsStudio ? "npm.cmd run dev  →  " + Project.DevScript
        : Project.DevScript != null ? "npx.cmd remotion studio  (the dev script runs " + Project.DevScript + ", not Studio)"
        : "npx.cmd remotion studio  (no dev script)";

    public string Tooltip => Project.Dir + "\n" + CommandText +
        (Project.Installed ? "" : "\nRemotion isn't installed in node_modules yet: run npm install in the folder first.") +
        (_running?.Address is { } a ? "\nStudio is running at " + a : _running != null ? "\nStudio is running (no address yet)" : "");

    public StudioInstance? Running
    {
        get => _running;
        set { _running = value; Raise(string.Empty); }
    }

    public string RunningText => _running?.Port is { } p ? "running · localhost:" + p : "running";
    public Visibility RunningVisibility => _running != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotInstalledVisibility => Project.Installed ? Visibility.Collapsed : Visibility.Visible;
}
