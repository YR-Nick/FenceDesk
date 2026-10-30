using System.Windows;
using System.Windows.Input;

namespace FenceDesk.Windows;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string value)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        ValueBox.Text = value;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    public static bool Show(string title, string prompt, string value, out string result)
    {
        var dialog = new InputDialog(title, prompt, value);
        var ok = dialog.ShowDialog() == true;
        result = dialog.Value;
        return ok && !string.IsNullOrWhiteSpace(result);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            ValidationText.Visibility = Visibility.Visible;
            ValueBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else
        {
            ValidationText.Visibility = Visibility.Collapsed;
        }
    }
}
