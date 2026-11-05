using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stowdeck.Services;

namespace Stowdeck;

/// <summary>
/// Builds a row's Tools dropdown: one item per tool that applies, each with a 16 px icon. Programs Windows has an icon
/// for (File Explorer, VS Code) use it; the others use a line icon from the system icon font, in the menu's text colour.
/// </summary>
internal static class ToolsMenu
{
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static ContextMenu Build(IReadOnlyList<ProjectTool>? tools, string? vsCode, Action<ProjectTool> run)
    {
        var menu = new ContextMenu();
        if (tools == null || tools.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "The folder can't be found", IsEnabled = false });
            return menu;
        }
        foreach (var tool in tools)
        {
            var item = new MenuItem
            {
                Header = tool.Title,
                InputGestureText = tool.Detail ?? "",
                Icon = IconFor(tool, vsCode),
            };
            AutomationProperties(item, tool);
            item.Click += (_, _) => run(tool);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Opens the menu under the button.</summary>
    public static void Show(ContextMenu menu, FrameworkElement button)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static void AutomationProperties(MenuItem item, ProjectTool tool) =>
        System.Windows.Automation.AutomationProperties.SetName(item, tool.Title + (tool.Detail != null ? ", " + tool.Detail : ""));

    private static UIElement IconFor(ProjectTool tool, string? vsCode)
    {
        string? program = tool.Kind switch
        {
            ToolKind.OpenFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            ToolKind.VSCode => vsCode,
            _ => null,
        };
        if (program != null && ProgramIcon(program) is { } image)
            return new Image { Source = image, Width = 16, Height = 16 };
        return Glyph(tool.Kind switch
        {
            ToolKind.OpenFolder => "",     // Folder
            ToolKind.VSCode => "",         // Code
            ToolKind.GitHub => "",         // Globe: opens a web page
            ToolKind.RemotionStudio => "", // Video
            _ => "",                       // Command prompt: runs npm run dev in a terminal
        });
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 14,
        Width = 16,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The program's own small icon, cached; null if Windows has none for it.</summary>
    private static ImageSource? ProgramIcon(string path)
    {
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? source = null;
        var handle = Native.SmallFileIcon(path);
        try
        {
            if (handle != IntPtr.Zero)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                bmp.Freeze();
                source = bmp;
            }
        }
        catch (Exception)
        {
            source = null;
        }
        finally
        {
            Native.FreeIcon(handle);
        }
        IconCache[path] = source;
        return source;
    }
}
