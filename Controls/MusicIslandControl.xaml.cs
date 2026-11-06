using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FenceDesk.Services;

namespace FenceDesk.Controls;

/// <summary>
/// 围栏桌面层里的音乐桌面精灵。作为 MainWindow 画布的子元素，
/// 和围栏一起挂在 WorkerW 上，因此回到桌面时不会消失，也不会盖住应用窗口。
/// </summary>
public partial class MusicIslandControl : UserControl, IDisposable
{
    private readonly AudioMeter _meter = new();
    private readonly MediaSessionService _mediaSession = new();
    private readonly DispatcherTimer _timer;
    private readonly List<Rectangle> _bars = new();
    private readonly double[] _phases = { 0.0, 1.1, 2.2, 0.7, 2.8, 1.7, 3.4 };
    private readonly TranslateTransform _eyeTranslate = new();
    private readonly RotateTransform _eyeRotate = new();
    private DateTime _lastAudibleUtc;
    private float _smoothed;
    private int _tick;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartLeft;
    private double _dragStartTop;

    public MusicIslandControl()
    {
        InitializeComponent();

        var accent = (Brush)FindResource("AccentBrush");
        for (var i = 0; i < 7; i++)
        {
            var bar = new Rectangle
            {
                Width = 4,
                Height = 5,
                RadiusX = 2,
                RadiusY = 2,
                Fill = accent,
                Opacity = 0.65 + i * 0.045,
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            _bars.Add(bar);
            BarsPanel.Children.Add(bar);
        }

        EyesCanvas.RenderTransform = new TransformGroup
        {
            Children = { _eyeTranslate, _eyeRotate },
        };
        EyesCanvas.RenderTransformOrigin = new Point(0.5, 0.5);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => UpdateAnimation();
        _timer.Start();
    }

    private void UpdateAnimation()
    {
        var now = DateTime.UtcNow;
        var peak = _meter.GetPeak();
        var playing = _mediaSession.IsPlaying();
        if (playing) _lastAudibleUtc = now;

        var audible = playing || now - _lastAudibleUtc < TimeSpan.FromSeconds(1.5);
        var targetWidth = audible ? 220.0 : 58.0;
        var targetHeight = audible ? 56.0 : 58.0;

        var newWidth = Width + (targetWidth - Width) * 0.18;
        var newHeight = Height + (targetHeight - Height) * 0.18;

        if (Parent is Canvas canvas)
        {
            var left = Canvas.GetLeft(this);
            if (double.IsNaN(left)) left = 0;
            var maxLeft = Math.Max(0, canvas.ActualWidth - newWidth);
            Canvas.SetLeft(this, Math.Clamp(left - (newWidth - Width) / 2, 0, maxLeft));
        }

        Width = newWidth;
        Height = newHeight;
        IslandBorder.CornerRadius = new CornerRadius(Height / 2);

        var targetOpacity = audible ? 1.0 : 0.0;
        BarsPanel.Opacity += (targetOpacity - BarsPanel.Opacity) * 0.2;

        _smoothed = Math.Max(peak, _smoothed * 0.84f);
        _tick++;

        var barArea = 28.0;
        for (var i = 0; i < _bars.Count; i++)
        {
            var wave = 0.62 + 0.38 * Math.Abs(Math.Sin(_tick * 0.16 + _phases[i]));
            var target = 4 + _smoothed * barArea * wave;
            var current = _bars[i].Height;
            _bars[i].Height = current + (target - current) * 0.45;
        }

        UpdateEyes(audible);
    }

    private void UpdateEyes(bool dancing)
    {
        try
        {
            if (NativeMethods.GetCursorPos(out var cursor))
            {
                var local = EyesCanvas.PointFromScreen(new Point(cursor.X, cursor.Y));
                MoveEye(EyeLeft, 10.5, 17, local);
                MoveEye(EyeRight, 32.5, 17, local);
            }
        }
        catch
        {
            // 窗口位置变化时忽略。
        }

        if (dancing)
        {
            var bounce = Math.Abs(Math.Sin(_tick * 0.22)) * (2 + _smoothed * 4);
            _eyeTranslate.Y = -bounce;
            _eyeRotate.Angle = Math.Sin(_tick * 0.11) * (2 + _smoothed * 4);
        }
        else
        {
            _eyeTranslate.Y += (0 - _eyeTranslate.Y) * 0.2;
            _eyeRotate.Angle += (0 - _eyeRotate.Angle) * 0.2;
        }
    }

    private static void MoveEye(FrameworkElement eye, double centerX, double centerY, Point cursor)
    {
        var dx = Math.Clamp((cursor.X - centerX) * 0.10, -3.2, 3.2);
        var dy = Math.Clamp((cursor.Y - centerY) * 0.08, -3.8, 3.8);
        Canvas.SetLeft(eye, centerX - eye.Width / 2 + dx);
        Canvas.SetTop(eye, centerY - eye.Height / 2 + dy);
    }

    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _dragging = true;
        _dragStart = e.GetPosition(Parent as IInputElement);
        _dragStartLeft = Canvas.GetLeft(this);
        _dragStartTop = Canvas.GetTop(this);
        if (double.IsNaN(_dragStartLeft)) _dragStartLeft = 0;
        if (double.IsNaN(_dragStartTop)) _dragStartTop = 0;
        CaptureMouse();
        e.Handled = true;
    }

    private void Island_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        if (Parent is not Canvas canvas) return;

        var current = e.GetPosition(canvas);
        var left = _dragStartLeft + current.X - _dragStart.X;
        var top = _dragStartTop + current.Y - _dragStart.Y;
        Canvas.SetLeft(this, Math.Clamp(left, 0, Math.Max(0, canvas.ActualWidth - Width)));
        Canvas.SetTop(this, Math.Clamp(top, 0, Math.Max(0, canvas.ActualHeight - Height)));
        e.Handled = true;
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    public void Dispose()
    {
        _timer.Stop();
        _meter.Dispose();
    }
}
