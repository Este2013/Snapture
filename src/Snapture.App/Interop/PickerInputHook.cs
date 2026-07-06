using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Snapture.App.Interop;

/// <summary>
/// Low-level keyboard + mouse hooks that give the (non-activating) picker overlay
/// its shortcuts without keyboard focus: Enter/Esc, R / Shift+R, Ctrl+Z / Ctrl+Y,
/// and the mouse wheel. Mapped inputs are swallowed so they don't leak to the app
/// underneath. Callbacks are marshalled to the UI thread; everything else passes
/// through untouched.
/// </summary>
internal sealed class PickerInputHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEWHEEL = 0x020A;

    private const int VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    private const int VK_R = 0x52, VK_Z = 0x5A, VK_Y = 0x59;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x; public int y; public uint mouseData; public uint flags; public uint time; public nint dwExtraInfo; }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    private readonly Dispatcher _dispatcher;
    private readonly Action _onEnter, _onEsc, _onRetake, _onHistoryBack, _onUndo, _onRedo;
    private readonly Action<int> _onWheel;

    // Keep the delegates alive for the hooks' lifetime.
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private nint _keyboardHook, _mouseHook;
    private bool _shift, _ctrl;

    public PickerInputHook(Dispatcher dispatcher, Action onEnter, Action onEsc,
        Action onRetake, Action onHistoryBack, Action onUndo, Action onRedo, Action<int> onWheel)
    {
        _dispatcher = dispatcher;
        _onEnter = onEnter; _onEsc = onEsc; _onRetake = onRetake;
        _onHistoryBack = onHistoryBack; _onUndo = onUndo; _onRedo = onRedo; _onWheel = onWheel;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, nint.Zero, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, nint.Zero, 0);
    }

    private nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if (vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT) { if (down) _shift = true; else if (up) _shift = false; }
            else if (vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL) { if (down) _ctrl = true; else if (up) _ctrl = false; }
            else if (down && TryMap(vk, out var action))
            {
                _dispatcher.BeginInvoke(action);
                return 1; // swallow so it never reaches the app underneath
            }
            else if (up && IsMapped(vk))
            {
                return 1; // swallow the matching key-up too
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool IsMapped(int vk) =>
        vk is VK_RETURN or VK_ESCAPE or VK_R || (_ctrl && vk is VK_Z or VK_Y);

    private bool TryMap(int vk, out Action action)
    {
        switch (vk)
        {
            case VK_RETURN: action = _onEnter; return true;
            case VK_ESCAPE: action = _onEsc; return true;
            case VK_R: action = _shift ? _onHistoryBack : _onRetake; return true;
            case VK_Z when _ctrl: action = _onUndo; return true;
            case VK_Y when _ctrl: action = _onRedo; return true;
            default: action = null!; return false;
        }
    }

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int delta = (short)(data.mouseData >> 16);
            _dispatcher.BeginInvoke(() => _onWheel(delta));
            return 1; // swallow: the wheel drives the picker, not the app
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != nint.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = nint.Zero; }
        if (_mouseHook != nint.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = nint.Zero; }
    }
}
