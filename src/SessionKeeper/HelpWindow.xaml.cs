using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper;

/// <summary>
/// The in-app help (the ? button next to Settings, or F1): a list of topics on the left and the selected topic on
/// the right. It opens on the topic for the tab you're on. Text only: nothing is launched from here.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow(string? sectionId = null)
    {
        InitializeComponent();
        SectionList.ItemsSource = HelpContent.Sections;
        SectionList.SelectedItem = HelpContent.For(sectionId);
        Loaded += (_, _) => (SectionList.ItemContainerGenerator.ContainerFromItem(SectionList.SelectedItem) as UIElement ?? SectionList).Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is HelpSection section) Show(section);
    }

    private void Show(HelpSection section)
    {
        Body.Children.Clear();
        Body.Children.Add(new TextBlock { Text = section.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Body.Children.Add(Paragraph(section.Summary, new Thickness(0, 6, 0, 4)));
        foreach (var block in section.Blocks)
        {
            if (block.Heading != null)
                Body.Children.Add(new TextBlock { Text = block.Heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
            for (int i = 0; i < block.Points.Count; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = new TextBlock { Text = block.Numbered ? $"{i + 1}." : "•", Foreground = (System.Windows.Media.Brush)FindResource("Muted") };
                var text = Paragraph(block.Points[i], new Thickness(0));
                Grid.SetColumn(text, 1);
                row.Children.Add(marker);
                row.Children.Add(text);
                Body.Children.Add(row);
            }
        }
        BodyScroll.ScrollToTop();
    }

    /// <summary>A wrapping paragraph; words between ** are bold.</summary>
    private static TextBlock Paragraph(string text, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = 19 };
        foreach (var (part, bold) in HelpContent.Runs(text))
            tb.Inlines.Add(new Run(part) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        return tb;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
