using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FenceDesk.Models;

namespace FenceDesk.Windows;

public partial class ArchiveProgressWindow : Window
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Storyboard _pulse;
    private long _lastBytes;
    private long _lastSampleTicks;
    private double _speed;
    private bool _indeterminate;
    private bool _finished;

    public ArchiveProgressWindow(string title, string target)
    {
        InitializeComponent();
        TitleText.Text = title;
        TargetText.Text = target;
        Progress = new Progress<ArchiveProgress>(Apply);

        var animation = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(620))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, Bar);
        Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));
        _pulse = new Storyboard();
        _pulse.Children.Add(animation);
    }

    /// <summary>在 UI 线程创建，解压线程上报后会自动切回 UI 线程。</summary>
    public IProgress<ArchiveProgress> Progress { get; }

    public event EventHandler? CancelRequested;

    public bool WasCancelled { get; private set; }

    public void RequestCancel()
    {
        if (_finished || WasCancelled) return;
        WasCancelled = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
        EntryText.Text = "正在停止…";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Finish()
    {
        _finished = true;
        _pulse.Stop();
        Bar.Opacity = 1;
        Bar.Value = 1;
    }

    private void Apply(ArchiveProgress progress)
    {
        if (_finished) return;

        if (progress.IsIndeterminate)
        {
            if (!_indeterminate)
            {
                _indeterminate = true;
                Bar.Value = 0;
                _pulse.Begin(this, true);
            }

            if (!string.IsNullOrWhiteSpace(progress.CurrentEntry)) EntryText.Text = progress.CurrentEntry;
            DetailText.Text = "正在处理，请稍候…";
            CancelButton.IsEnabled = !WasCancelled;
            return;
        }

        if (_indeterminate)
        {
            _indeterminate = false;
            _pulse.Stop();
            Bar.Opacity = 1;
        }

        Bar.Value = progress.Fraction;
        if (!string.IsNullOrWhiteSpace(progress.CurrentEntry)) EntryText.Text = progress.CurrentEntry;

        var now = _clock.ElapsedMilliseconds;
        if (now - _lastSampleTicks >= 400)
        {
            var elapsed = (now - _lastSampleTicks) / 1000.0;
            if (elapsed > 0 && progress.WrittenBytes >= _lastBytes)
            {
                var instant = (progress.WrittenBytes - _lastBytes) / elapsed;
                _speed = _speed <= 0 ? instant : (_speed * 0.65 + instant * 0.35);
            }

            _lastBytes = progress.WrittenBytes;
            _lastSampleTicks = now;
        }

        var parts = new List<string> { progress.Percent + "%" };
        if (progress.TotalEntries > 0) parts.Add(progress.CompletedEntries + "/" + progress.TotalEntries + " 个文件");
        if (progress.TotalBytes > 0) parts.Add(FormatSize(progress.WrittenBytes) + " / " + FormatSize(progress.TotalBytes));
        if (_speed > 0) parts.Add(FormatSize((long)_speed) + "/s");
        DetailText.Text = string.Join(" · ", parts);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.##") + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_finished)
        {
            // 关闭窗口时先请求取消，避免后台线程继续写文件而用户以为已经停止。
            RequestCancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
