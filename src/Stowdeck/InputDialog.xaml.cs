using System.Windows;
using System.Windows.Interop;
using Stowdeck.Services;

namespace Stowdeck;

public partial class InputDialog : Window
{
    public string Value => ValueBox.Text;

    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ValueBox.Text.Trim().Length == 0) return;
        DialogResult = true;
    }
}
