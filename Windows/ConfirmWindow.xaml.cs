using System.Windows;
using System.Windows.Input;

namespace FenceDesk.Windows;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string yesText, string noText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = yesText;
        NoButton.Content = noText;
        Loaded += (_, _) => YesButton.Focus();
    }

    public static bool Ask(string title, string message, string yesText = "确定", string noText = "取消")
    {
        var window = new ConfirmWindow(title, message, yesText, noText);
        return window.ShowDialog() == true;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
