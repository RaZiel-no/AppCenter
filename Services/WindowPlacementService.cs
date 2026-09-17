using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AppCenter.Services;

/// <summary>
/// Where the main window was when it was last closed: the rectangle it
/// restores to, and whether it was maximised over it.
///
/// The rectangle is in screen pixels rather than WPF's scaled units. A pixel
/// means the same thing on every monitor and a unit does not - on a monitor
/// at 125% a unit is a pixel and a quarter - so a size kept in units would
/// come back a size larger or smaller wherever the scaling differed from
/// last time. In pixels the window comes back exactly where it was, on the
/// monitor it was on.
/// </summary>
public sealed class WindowPlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public static class WindowPlacementService
{
    /// <summary>
    /// How much of the window has to be on a monitor for its old place to be
    /// worth going back to. A monitor unplugged since last time leaves the
    /// window on nothing at all; a screen that shrank can leave a sliver too
    /// thin to grab. Either way the caller's default place is better.
    /// </summary>
    private const int VisibleMargin = 64;

    /// <summary>
    /// Where <paramref name="window"/> is now, in the form that goes in the
    /// settings file, or null before it has a handle to ask about.
    /// </summary>
    public static WindowPlacement? Capture(Window window)
    {
        // RestoreBounds is the normal rectangle whatever the state - the one
        // under a maximised window, the one a minimised window comes back to
        // - in units at the window's own scaling, so scaling it back gives
        // the pixels exactly.
        var bounds = window.RestoreBounds;
        if (bounds.IsEmpty)
            return null;

        var dpi = VisualTreeHelper.GetDpi(window);

        return new WindowPlacement
        {
            Left = (int)Math.Round(bounds.X * dpi.DpiScaleX),
            Top = (int)Math.Round(bounds.Y * dpi.DpiScaleY),
            Width = (int)Math.Round(bounds.Width * dpi.DpiScaleX),
            Height = (int)Math.Round(bounds.Height * dpi.DpiScaleY),
            Maximized = window.WindowState == WindowState.Maximized,
        };
    }

    /// <summary>
    /// Puts <paramref name="window"/> back at <paramref name="saved"/>. Meant
    /// for OnSourceInitialized - the handle exists, the window is not yet on
    /// screen - so it appears in place rather than jumping there. Only the
    /// rectangle: maximising is the caller's, since it applies whether or
    /// not the rectangle could be used. False when there is nothing saved
    /// or the saved place is on no monitor now attached.
    /// </summary>
    public static bool Restore(Window window, WindowPlacement? saved)
    {
        if (saved is null || saved.Width <= 0 || saved.Height <= 0)
            return false;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        var visible = new RECT
        {
            Left = saved.Left + VisibleMargin,
            Top = saved.Top + VisibleMargin,
            Right = saved.Left + saved.Width - VisibleMargin,
            Bottom = saved.Top + saved.Height - VisibleMargin,
        };

        if (MonitorFromRect(ref visible, MONITOR_DEFAULTTONULL) == IntPtr.Zero)
            return false;

        // Landing on a monitor with a different scaling from the one the
        // window was created on, WPF resizes it to keep its size in units -
        // the right thing when a window is dragged across, the wrong thing
        // here. Now that the window is on that monitor a second move sticks.
        var dpi = VisualTreeHelper.GetDpi(window);
        Move(handle, saved);

        if (!VisualTreeHelper.GetDpi(window).Equals(dpi))
            Move(handle, saved);

        return true;
    }

    private static void Move(IntPtr handle, WindowPlacement saved) =>
        SetWindowPos(handle, IntPtr.Zero, saved.Left, saved.Top, saved.Width, saved.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    private const int MONITOR_DEFAULTTONULL = 0;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, int flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, int flags);
}
