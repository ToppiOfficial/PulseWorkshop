using System.Windows;

namespace SrcWorkshop.App.Views;

/// <summary>Small modal prompt for a single line of text (e.g. naming a template).</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    public string Value => InputBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>Shows the prompt and returns the entered text, or null if cancelled/empty.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new TextPromptWindow(title, prompt, initial) { Owner = owner };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
            return dlg.Value.Trim();
        return null;
    }
}
