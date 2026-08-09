namespace Blackcat.NineLives.Models;

/// <summary>
/// Where the window was, so it comes back there (#211).
///
/// The app forgot its size, position and screen on every launch - the small daily tax everyone
/// pays without mentioning. Captured on close, applied before first paint, and never trusted
/// blindly: a position saved on a monitor that has since been unplugged would put the window
/// somewhere nobody can click.
/// </summary>
public sealed class WindowGeometry
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }

    /// <summary>
    /// Whether this position still puts something clickable on a screen.
    ///
    /// The test is the title bar: at least a hand's width of the window's top edge must lie
    /// within the virtual screen, because the title bar is how a window in the wrong place gets
    /// dragged to the right one. A window that is merely partly off-screen is fine - people park
    /// windows half-off deliberately.
    /// </summary>
    public bool IsUsableOn(double virtualLeft, double virtualTop, double virtualWidth, double virtualHeight)
    {
        if (Width < 200 || Height < 200) return false;

        const double grabbable = 100;

        var virtualRight = virtualLeft + virtualWidth;
        var virtualBottom = virtualTop + virtualHeight;

        // Some slice of the top edge, at least `grabbable` wide, inside the virtual bounds.
        var overlapLeft = Math.Max(Left, virtualLeft);
        var overlapRight = Math.Min(Left + Width, virtualRight);

        var titleBarVisible =
            overlapRight - overlapLeft >= grabbable &&
            Top >= virtualTop - 8 &&          // -8: maximised windows sit slightly above 0
            Top <= virtualBottom - grabbable; // and the bar itself must not be below the bottom

        return titleBarVisible;
    }
}
