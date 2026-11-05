using System.Windows;
using Stowdeck.Services;

namespace Stowdeck.ViewModels;

/// <summary>A row with a project folder: it gets a Tools button and the "uncommitted changes" badge.</summary>
public interface IProjectRow
{
    /// <summary>The project folder, or null when none is recorded.</summary>
    string? ProjectFolder { get; }
    bool FolderMissing { get; }
    GitBadge Git { get; }
}

public static class ProjectRow
{
    public static bool CanUseTools(IProjectRow row) => !string.IsNullOrWhiteSpace(row.ProjectFolder) && !row.FolderMissing;

    public static string ToolsTooltip(IProjectRow row) =>
        string.IsNullOrWhiteSpace(row.ProjectFolder) ? "No project folder recorded"
        : row.FolderMissing ? "The project folder is missing"
        : "Tools for this project: open its folder, editor, GitHub page or dev server";
}

/// <summary>The badge's state for one row; set from the background git check (see <see cref="GitStatusCache"/>).</summary>
public sealed class GitBadge : Observable
{
    private GitState? _state;

    public GitState? State
    {
        get => _state;
        set
        {
            if (ReferenceEquals(_state, value) || Equals(_state, value)) return;
            _state = value;
            Raise(string.Empty);
        }
    }

    public Visibility Visibility => _state?.HasWork == true ? Visibility.Visible : Visibility.Collapsed;
    public string Text => _state?.BadgeText ?? "";
    public string Tooltip => _state?.Describe() ?? "";
}
