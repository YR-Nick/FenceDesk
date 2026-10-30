using System.Runtime.InteropServices;
using System.Windows;

namespace FenceDesk.Services;

/// <summary>
/// 低层鼠标钩子，用于识别“在桌面空白处双击”（类似 Fences 的双击隐藏）。
/// 事件在钩子线程触发，订阅者需要自行切回 UI 线程。
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private long _lastClickTime;
    private NativeMethods.POINT _lastClickPoint;
    private bool _disposed;

    public event Action<Point>? PossibleDesktopDoubleClick;
    public event Action? ShowDesktopClicked;

    public MouseHook()
    {
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            LogService.Error("注册鼠标钩子失败，双击桌面隐藏不可用。Win32=" + Marshal.GetLastWin32Error());
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == new IntPtr(NativeMethods.WM_LBUTTONDOWN))
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (IsShowDesktopButton(data.pt))
                {
                    _lastClickTime = 0;
                    ShowDesktopClicked?.Invoke();
                }
                else
                {
                    var now = Environment.TickCount64;
                    var threshold = Math.Max(250, NativeMethods.GetDoubleClickTime());
                    var dx = data.pt.X - _lastClickPoint.X;
                    var dy = data.pt.Y - _lastClickPoint.Y;

                    if (now - _lastClickTime <= threshold && dx * dx + dy * dy <= 36)
                    {
                        _lastClickTime = 0;
                        PossibleDesktopDoubleClick?.Invoke(new Point(data.pt.X, data.pt.Y));
                    }
                    else
                    {
                        _lastClickTime = now;
                        _lastClickPoint = data.pt;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("鼠标钩子回调异常", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsShowDesktopButton(NativeMethods.POINT point)
    {
        try
        {
            var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;

            var notify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            var button = notify != IntPtr.Zero
                ? NativeMethods.FindWindowEx(notify, IntPtr.Zero, "TrayShowDesktopButtonWClass", null)
                : IntPtr.Zero;
            if (button == IntPtr.Zero)
            {
                button = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayShowDesktopButtonWClass", null);
            }
            if (button == IntPtr.Zero)
            {
                button = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayShowDesktopButtonWnd", null);
            }
            if (button == IntPtr.Zero) return false;

            if (!NativeMethods.GetWindowRect(button, out var rect)) return false;
            return point.X >= rect.Left && point.X <= rect.Right &&
                   point.Y >= rect.Top && point.Y <= rect.Bottom;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
