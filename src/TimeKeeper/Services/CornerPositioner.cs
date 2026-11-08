using System.Windows;
using System.Windows.Interop;
using TimeKeeper.Interop;
using TimeKeeper.Models;
using Screen = System.Windows.Forms.Screen;

namespace TimeKeeper.Services;

/// <summary>
/// Places the corner window flush into a corner of a monitor's work area.
/// Works in physical pixels via SetWindowPos so mixed-DPI setups behave:
/// we first move the window onto the target monitor (letting WPF rescale it
/// for that monitor's DPI), then read its real pixel size and snap.
/// </summary>
public static class CornerPositioner
{
    /// <summary>Resolve the saved monitor, or the primary one if it's gone.</summary>
    public static Screen ResolveScreen(CornerPlacement placement, out bool fellBack)
    {
        fellBack = false;
        if (!string.IsNullOrEmpty(placement.MonitorDeviceName))
        {
            var match = Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, placement.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            fellBack = true;
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    public static void Place(Window window, Corner corner, Screen screen)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var area = screen.WorkingArea;
        const uint flags = Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;

        // Step 1: drop the window inside the target monitor so WM_DPICHANGED
        // resizes it for that monitor's scale.
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, area.Left + area.Width / 2, area.Top + area.Height / 2, 0, 0, flags);

        // Step 2: now its pixel size is right for this monitor; snap flush.
        Native.GetWindowRect(hwnd, out var rect);
        var x = corner is Corner.BottomRight or Corner.TopRight ? area.Right - rect.Width : area.Left;
        var y = corner is Corner.BottomRight or Corner.BottomLeft ? area.Bottom - rect.Height : area.Top;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, flags);

        Native.GetWindowRect(hwnd, out var final);
        Log.Info($"Placed {window.GetType().Name} {corner} on {screen.DeviceName}: work area {area}, " +
                 $"window {final.Left},{final.Top} {final.Width}x{final.Height}, dpi {Native.GetDpiForWindow(hwnd)}, " +
                 $"state {window.WindowState}, visible {window.IsVisible}");
    }

    /// <summary>
    /// After a drag: which monitor is the window's centre on, and which of its
    /// four corners is closest.
    /// </summary>
    public static (Screen Screen, Corner Corner) NearestCorner(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Native.GetWindowRect(hwnd, out var rect);
        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;

        var screen = Screen.FromPoint(new System.Drawing.Point(cx, cy));
        var area = screen.WorkingArea;
        var right = cx >= area.Left + area.Width / 2;
        var bottom = cy >= area.Top + area.Height / 2;

        var corner = (right, bottom) switch
        {
            (true, true) => Corner.BottomRight,
            (false, true) => Corner.BottomLeft,
            (true, false) => Corner.TopRight,
            _ => Corner.TopLeft,
        };
        return (screen, corner);
    }

    public static bool IsPrimary(Screen screen) => screen.Primary;
}
