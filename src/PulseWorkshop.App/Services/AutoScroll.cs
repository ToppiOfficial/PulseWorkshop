using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Browser-style middle-click autoscroll for every ScrollViewer in the app: press the wheel and the
/// view scrolls in the direction you move the mouse, faster the further you go. Released without
/// moving, it sticks (move to scroll, click anywhere or press Escape to stop); released after
/// moving, it stops there.
///
/// Installed once with <see cref="Install"/> as a class handler on ScrollViewer, so it applies to
/// every list, tree and scrolling panel - including ones added later - with no per-control opt-in.
/// </summary>
public static class AutoScroll
{
    // Mouse travel (px) around the origin that scrolls nothing, so a shaky hand stays put.
    private const double DeadZone = 12;

    // Divides the cursor distance to pixels-per-tick; larger = slower. At 60 ticks/s a 100px pull
    // scrolls roughly (100-12)/8 * 60 = ~660 px/s.
    private const double Damping = 8;

    private static ScrollViewer? _target;
    private static Point _origin;
    private static bool _moved;
    private static double _offsetX, _offsetY;
    private static DispatcherTimer? _timer;
    private static Cursor? _savedCursor;

    /// <summary>Hooks the app-wide handlers. Call once at startup.</summary>
    public static void Install()
    {
        // handledEventsToo: some controls (list items, buttons) mark mouse-down handled; the wheel
        // button is never their business, so we want it regardless.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseDownEvent,
            new MouseButtonEventHandler(OnMouseDown), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseUpEvent,
            new MouseButtonEventHandler(OnMouseUp), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseMoveEvent,
            new MouseEventHandler(OnMouseMove), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.LostMouseCaptureEvent,
            new MouseEventHandler((_, _) => Stop()), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent,
            new KeyEventHandler((_, e) => { if (e.Key == Key.Escape) Stop(); }), true);
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Any click while sticky-scrolling just stops it (browser behaviour).
        if (_target is not null)
        {
            Stop();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle || sender is not ScrollViewer sv)
            return;
        // Bubbling reaches the innermost ScrollViewer first; ignore the outer ones for this press,
        // and anything with nothing to scroll.
        if (sv.ScrollableHeight <= 0 && sv.ScrollableWidth <= 0)
            return;

        _target = sv;
        _origin = e.GetPosition(sv);
        _moved = false;
        _offsetX = sv.HorizontalOffset;
        _offsetY = sv.VerticalOffset;
        _savedCursor = sv.Cursor;
        sv.Cursor = Cursors.ScrollAll;
        sv.CaptureMouse();

        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += Tick;
        }
        _timer.Start();
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_target is null)
            return;
        // Past the dead zone counts as a drag, so releasing the button ends the scroll instead of
        // leaving it sticky.
        var p = e.GetPosition(_target);
        if (Math.Abs(p.X - _origin.X) > DeadZone || Math.Abs(p.Y - _origin.Y) > DeadZone)
            _moved = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_target is null || e.ChangedButton != MouseButton.Middle)
            return;
        if (_moved)
            Stop();
        e.Handled = true;
    }

    private static void Tick(object? sender, EventArgs e)
    {
        if (_target is not { } sv)
        {
            Stop();
            return;
        }

        var p = Mouse.GetPosition(sv);
        _offsetY = Advance(_offsetY, p.Y - _origin.Y, sv.ScrollableHeight, UnitSize(sv.ActualHeight, sv.ViewportHeight, sv.CanContentScroll));
        _offsetX = Advance(_offsetX, p.X - _origin.X, sv.ScrollableWidth, UnitSize(sv.ActualWidth, sv.ViewportWidth, sv.CanContentScroll));
        sv.ScrollToVerticalOffset(_offsetY);
        sv.ScrollToHorizontalOffset(_offsetX);
    }

    /// <summary>Moves <paramref name="offset"/> by one tick's worth of the cursor's <paramref name="distance"/>
    /// from the origin, clamped to [0, <paramref name="scrollable"/>]. The offset is kept as a double
    /// across ticks so sub-unit speeds still accumulate.</summary>
    private static double Advance(double offset, double distance, double scrollable, double unitSize)
    {
        var beyond = Math.Abs(distance) - DeadZone;
        if (beyond > 0)
            offset += Math.Sign(distance) * beyond / Damping / unitSize;
        return Math.Clamp(offset, 0, scrollable);
    }

    /// <summary>Pixels per unit of scroll offset: 1 for pixel scrolling, one item's height/width when
    /// the ScrollViewer scrolls by item (virtualizing lists), so both feel the same speed.</summary>
    private static double UnitSize(double actualPixels, double viewportUnits, bool canContentScroll)
    {
        if (!canContentScroll || viewportUnits <= 0 || actualPixels <= 0)
            return 1;
        return actualPixels / viewportUnits;
    }

    private static void Stop()
    {
        _timer?.Stop();
        if (_target is { } sv)
        {
            _target = null; // cleared first: ReleaseMouseCapture re-enters here via LostMouseCapture.
            sv.Cursor = _savedCursor;
            sv.ReleaseMouseCapture();
        }
    }
}
