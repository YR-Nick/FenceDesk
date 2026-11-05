using System.Windows;
using System.Windows.Input;

namespace FenceDesk.Windows;

public partial class PasswordDialog : Window
{
    private readonly bool _confirm;

    private PasswordDialog(string title, string message, bool confirm)
    {
        InitializeComponent();
        _confirm = confirm;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmInput.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public static string? Ask(string title, string message, bool confirm = false)
    {
        var dialog = new PasswordDialog(title, message, confirm);
        return dialog.ShowDialog() == true ? dialog.PasswordInput.Password : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_confirm && PasswordInput.Password != ConfirmInput.Password)
        {
            ErrorText.Text = "两次输入的密码不一致";
            return;
        }

        if (_confirm && PasswordInput.Password.Length is > 0 and < 4)
        {
            ErrorText.Text = "密码至少 4 位，或留空取消密码";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
