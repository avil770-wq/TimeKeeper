using System.Windows.Input;
using System.Windows.Interop;
using TimeKeeper.Interop;
using TimeKeeper.Models;

namespace TimeKeeper.Services;

/// <summary>
/// One global hotkey, registered on a message-only window so it works even
/// while the corner is hidden.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5444; // "TD"
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("TimeKeeperHotkeySink")
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public HotkeySetting? Current { get; private set; }

    /// <summary>
    /// Replace the active hotkey. Returns false if the combination is taken
    /// by another app; in that case no hotkey is active.
    /// </summary>
    public bool TryRegister(HotkeySetting hotkey)
    {
        Unregister();
        if (hotkey.IsEmpty) return true;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        var ok = Native.RegisterHotKey(_source.Handle, HotkeyId, ToNativeModifiers(hotkey.Modifiers) | Native.MOD_NOREPEAT, vk);
        if (ok)
        {
            _registered = true;
            Current = hotkey.Clone();
            Log.Info($"Hotkey registered: {hotkey}");
        }
        else
        {
            Log.Warn($"Hotkey {hotkey} is taken (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }

        return ok;
    }

    public void Unregister()
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        Current = null;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(ModifierKeys m)
    {
        uint result = 0;
        if (m.HasFlag(ModifierKeys.Alt)) result |= Native.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Control)) result |= Native.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Shift)) result |= Native.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) result |= Native.MOD_WIN;
        return result;
    }
}
