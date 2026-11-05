using System.Runtime.InteropServices;

namespace FenceDesk.Services;

public enum FenceShortcut
{
    Delete,
    Copy,
    Paste,
}

/// <summary>
/// 全局键盘快捷键。只在桌面/围栏是前台且存在选中项目时由 MainWindow 接受，
/// 不会拦截资源管理器或其他应用里的 Del / Ctrl+C / Ctrl+V。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkDelete = 0x2E;
    private const int VkC = 0x43;
    private const int VkV = 0x56;

    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private long _lastHandledTick;
    private bool _disposed;

    public Func<FenceShortcut, bool>? TryHandle { get; set; }

    public KeyboardHook()
    {
        _proc = Callback;
        _hook = NativeMethods.SetWindowsHookExKeyboard(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            LogService.Error("注册键盘快捷键钩子失败。Win32=" + Marshal.GetLastWin32Error());
        }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == new IntPtr(0x0100) || wParam == new IntPtr(0x0104)))
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var control = (NativeMethods.GetKeyState(VkControl) & 0x8000) != 0;
                var alt = (NativeMethods.GetKeyState(VkMenu) & 0x8000) != 0;

                FenceShortcut? shortcut = null;
                if (control && !alt && data.vkCode == VkC)
                {
                    shortcut = FenceShortcut.Copy;
                }
                else if (control && !alt && data.vkCode == VkV)
                {
                    shortcut = FenceShortcut.Paste;
                }
                else if (!control && !alt && data.vkCode == VkDelete)
                {
                    shortcut = FenceShortcut.Delete;
                }

                if (shortcut is not null)
                {
                    var now = Environment.TickCount64;
                    if (now - _lastHandledTick > 350 &&
                        TryHandle?.Invoke(shortcut.Value) == true)
                    {
                        _lastHandledTick = now;
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("键盘快捷键回调异常", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
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
