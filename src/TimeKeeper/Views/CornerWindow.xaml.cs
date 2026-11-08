using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TimeKeeper.Interop;
using TimeKeeper.Models;
using Point = System.Windows.Point;

namespace TimeKeeper.Views;

public enum CornerState
{
    Idle,
    Running,
    Pending,
}

/// <summary>
/// The always-on-top quarter circle. Collapsed it's a 14 DIP sliver;
/// on hover it grows to 64 DIP and shows the icon.
/// </summary>
public partial class CornerWindow : Window
{
    private const double CollapsedRadius = 14;
    private const double ExpandedRadius = 64;
    private const double Size = 64;
    private const double IconInset = 12;
    private const double IconSize = 24;

    private Corner _corner = Corner.BottomRight;
    private Point? _pressPoint;
    private bool _menuOpen;

    public CornerWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        MouseEnter += (_, _) => Expand(true);
        MouseLeave += (_, _) => { if (!_menuOpen) Expand(false); };
        Quarter.MouseLeftButtonDown += OnPress;
        Quarter.MouseMove += OnMove;
        Quarter.MouseLeftButtonUp += OnRelease;
        Quarter.MouseRightButtonUp += OnRightClick;
    }

    /// <summary>Left click (not a drag).</summary>
    public event EventHandler? CornerClicked;

    /// <summary>Dropped after a drag; App decides where to snap.</summary>
    public event EventHandler? CornerDropped;

    /// <summary>Asks App for a fresh context menu each time.</summary>
    public Func<ContextMenu>? ContextMenuFactory { get; set; }

    public Corner Corner
    {
        get => _corner;
        set
        {
            _corner = value;
            LayoutForCorner();
        }
    }

    public void SetState(CornerState state)
    {
        var key = state switch
        {
            CornerState.Running => "CornerRunBrush",
            CornerState.Pending => "CornerPendingBrush",
            _ => "CornerIdleBrush",
        };
        var brush = (Brush)FindResource(key);
        Quarter.Fill = brush;
        OuterLine.Fill = brush;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Tool window: no taskbar button, not in Alt+Tab. No-activate: clicking
        // the corner doesn't steal focus from whatever the user is typing in.
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        ex &= ~(long)Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void LayoutForCorner()
    {
        var center = _corner switch
        {
            Corner.BottomRight => new Point(Size, Size),
            Corner.BottomLeft => new Point(0, Size),
            Corner.TopRight => new Point(Size, 0),
            _ => new Point(0, 0),
        };
        Shape.Center = center;
        OuterShape.Center = center;

        var right = _corner is Corner.BottomRight or Corner.TopRight;
        var bottom = _corner is Corner.BottomRight or Corner.BottomLeft;
        Canvas.SetLeft(CornerIcon, right ? Size - IconInset - IconSize : IconInset);
        Canvas.SetTop(CornerIcon, bottom ? Size - IconInset - IconSize : IconInset);
    }

    private void Expand(bool expand)
    {
        var radius = expand ? ExpandedRadius : CollapsedRadius;
        var opacity = expand ? 1.0 : 0.0;

        // Respect "Animation effects" off in Windows settings.
        if (!SystemParameters.ClientAreaAnimation)
        {
            foreach (var g in new[] { Shape, OuterShape })
            {
                g.BeginAnimation(EllipseGeometry.RadiusXProperty, null);
                g.BeginAnimation(EllipseGeometry.RadiusYProperty, null);
                g.RadiusX = g.RadiusY = radius;
            }

            CornerIcon.BeginAnimation(OpacityProperty, null);
            CornerIcon.Opacity = opacity;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var g in new[] { Shape, OuterShape })
        {
            g.BeginAnimation(EllipseGeometry.RadiusXProperty, new DoubleAnimation(radius, duration) { EasingFunction = ease });
            g.BeginAnimation(EllipseGeometry.RadiusYProperty, new DoubleAnimation(radius, duration) { EasingFunction = ease });
        }

        CornerIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(expand ? 160 : 80))
        {
            BeginTime = expand ? TimeSpan.FromMilliseconds(60) : TimeSpan.Zero,
        });
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        Quarter.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressPoint = null;
        Quarter.ReleaseMouseCapture();
        try
        {
            DragMove(); // blocks until the button is released
        }
        catch (InvalidOperationException)
        {
            // Button already released.
        }

        CornerDropped?.Invoke(this, EventArgs.Empty);
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        Quarter.ReleaseMouseCapture();
        if (_pressPoint is null) return;
        _pressPoint = null;
        e.Handled = true;
        CornerClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ContextMenuFactory is null) return;
        var menu = ContextMenuFactory();
        menu.PlacementTarget = Quarter;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!IsMouseOver) Expand(false);
        };
        menu.IsOpen = true;
        e.Handled = true;
    }
}
