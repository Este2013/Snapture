using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Snapture.App.Interop;

/// <summary>
/// Registers system-wide hotkeys via a message-only window. Each hotkey fires its
/// action on the UI thread. Bare function keys are global, so registration can
/// fail if another app already owns the combo — callers get a bool back.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    // Common virtual-key codes.
    public const uint VK_F6 = 0x75;
    public const uint VK_F7 = 0x76;
    public const uint VK_RETURN = 0x0D;
    public const uint VK_ESCAPE = 0x1B;

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly nint HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private readonly Dictionary<int, Action> _actions = new();
    private HwndSource? _source;
    private int _nextId = 1;

    public void Initialize()
    {
        var p = new HwndSourceParameters("SnaptureHotkeys")
        {
            Width = 0,
            Height = 0,
            ParentWindow = HWND_MESSAGE,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Register a global hotkey. Returns false if the combo is unavailable.</summary>
    public bool Register(uint virtualKey, Action action, uint modifiers = 0)
    {
        if (_source is null) throw new InvalidOperationException("Call Initialize() first.");
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey))
            return false;
        _actions[id] = action;
        return true;
    }

    /// <summary>
    /// Register a hotkey and return its id so it can be individually removed with
    /// <see cref="Unregister"/> — used for keys that are only live while the picker
    /// overlay is open, without disturbing the persistent app hotkeys. Returns 0
    /// if the combo is unavailable.
    /// </summary>
    public int RegisterScoped(uint virtualKey, Action action, uint modifiers = 0)
    {
        if (_source is null) throw new InvalidOperationException("Call Initialize() first.");
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey))
            return 0;
        _actions[id] = action;
        return id;
    }

    /// <summary>Unregister a single hotkey previously returned by <see cref="RegisterScoped"/>.</summary>
    public void Unregister(int id)
    {
        if (_source is null || id == 0) return;
        if (_actions.Remove(id))
            try { UnregisterHotKey(_source.Handle, id); } catch { }
    }

    /// <summary>Unregister all current hotkeys (keeps the message window for re-use).</summary>
    public void Clear()
    {
        if (_source is null) return;
        foreach (var id in _actions.Keys)
            try { UnregisterHotKey(_source.Handle, id); } catch { }
        _actions.Clear();
        _nextId = 1;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;
        foreach (var id in _actions.Keys)
            try { UnregisterHotKey(_source.Handle, id); } catch { }
        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
