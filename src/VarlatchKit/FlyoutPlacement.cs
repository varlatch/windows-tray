namespace VarlatchKit;

/// <summary>A rectangle in screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Where the panel opens: next to the tray icon, on the taskbar's side of
/// the screen, inside the work area, the way Windows places its own
/// flyouts.
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>The taskbar's edge: the side where the monitor reaches past the work area.</summary>
    public static TaskbarEdge Edge(PixelRect monitor, PixelRect workArea)
    {
        if (workArea.Top > monitor.Top) return TaskbarEdge.Top;
        if (workArea.Left > monitor.Left) return TaskbarEdge.Left;
        if (workArea.Right < monitor.Right) return TaskbarEdge.Right;
        return TaskbarEdge.Bottom;
    }

    /// <summary>
    /// The panel's position. <paramref name="anchor"/> is the tray icon, or
    /// null when Windows does not say where it is (it is in the overflow
    /// menu): then the corner of the work area by the clock.
    /// </summary>
    /// <param name="gap">Pixels between the panel and the taskbar or the work area's edge.</param>
    public static PixelRect Place(PixelRect? anchor, PixelRect monitor, PixelRect workArea, int width, int height, int gap)
    {
        var edge = Edge(monitor, workArea);
        width = Math.Min(width, workArea.Width - 2 * gap);
        height = Math.Min(height, workArea.Height - 2 * gap);
        int x, y;
        if (anchor is { IsEmpty: false } icon)
        {
            switch (edge)
            {
                case TaskbarEdge.Bottom:
                    x = icon.CenterX - width / 2;
                    y = workArea.Bottom - gap - height;
                    break;
                case TaskbarEdge.Top:
                    x = icon.CenterX - width / 2;
                    y = workArea.Top + gap;
                    break;
                case TaskbarEdge.Left:
                    x = workArea.Left + gap;
                    y = icon.CenterY - height / 2;
                    break;
                default:
                    x = workArea.Right - gap - width;
                    y = icon.CenterY - height / 2;
                    break;
            }
        }
        else
        {
            x = edge == TaskbarEdge.Left ? workArea.Left + gap : workArea.Right - gap - width;
            y = edge == TaskbarEdge.Top ? workArea.Top + gap : workArea.Bottom - gap - height;
        }
        x = Math.Clamp(x, workArea.Left + gap, workArea.Right - gap - width);
        y = Math.Clamp(y, workArea.Top + gap, workArea.Bottom - gap - height);
        return new PixelRect(x, y, x + width, y + height);
    }
}
