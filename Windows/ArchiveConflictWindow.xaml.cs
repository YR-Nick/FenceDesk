using System.Windows;
using System.Windows.Input;
using FenceDesk.Models;

namespace FenceDesk.Windows;

public partial class ArchiveConflictWindow : Window
{
    private readonly ArchiveConflict _conflict;

    private ArchiveConflictWindow(ArchiveConflict conflict)
    {
        InitializeComponent();
        _conflict = conflict;

        NameText.Text = conflict.EntryPath;
        NameText.ToolTip = conflict.TargetPath;
        if (conflict.TargetIsDirectory)
        {
            ExistingText.Text = "目标位置是一个同名文件夹";
        }
        else
        {
            ExistingText.Text = "已存在：" + ArchiveProgressWindow.FormatSize(conflict.ExistingSize);
        }

        IncomingText.Text = "解压后：" + ArchiveProgressWindow.FormatSize(conflict.EntrySize);
        Loaded += (_, _) => SkipButton.Focus();
    }

    public ArchiveConflictDecision Decision { get; private set; } = ArchiveConflictDecision.Cancel;

    public bool ApplyToAll => ApplyAllBox.IsChecked == true;

    public static ArchiveConflictAnswer Ask(ArchiveConflict conflict)
    {
        var window = new ArchiveConflictWindow(conflict);
        window.ShowDialog();
        return new ArchiveConflictAnswer(window.Decision, window.ApplyToAll);
    }

    private void Accept(ArchiveConflictDecision decision)
    {
        Decision = decision;
        DialogResult = true;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e) => Accept(ArchiveConflictDecision.Overwrite);

    private void Skip_Click(object sender, RoutedEventArgs e) => Accept(ArchiveConflictDecision.Skip);

    private void KeepBoth_Click(object sender, RoutedEventArgs e) => Accept(ArchiveConflictDecision.KeepBoth);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Accept(ArchiveConflictDecision.Cancel);

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Accept(ArchiveConflictDecision.Cancel);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DialogResult is null) Decision = ArchiveConflictDecision.Cancel;
    }
}
