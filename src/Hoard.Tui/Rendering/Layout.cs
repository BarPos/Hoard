namespace Hoard.Tui.Rendering;

/// <summary>
/// Responsive layout (plan.md §7.10), shared by the renderer and mouse hit-testing.
/// Rows: 0 header · 1 breadcrumbs · 2 box top · 3 column headings · 4.. list · H-2 box bottom · H-1 footer.
/// </summary>
public sealed record Layout(int Width, int Height)
{
    public const int MinWidth = 60;
    public const int MinHeight = 16;

    public bool TooSmall => Width < MinWidth || Height < MinHeight;

    /// <summary>Details pane beside the list from 100 columns.</summary>
    public bool ShowDetails => Width >= 100;

    public int DetailsWidth => ShowDetails ? Math.Clamp(Width * 32 / 100, 31, 44) : 0;

    /// <summary>Content width of the list pane (inside the borders and padding).</summary>
    public int ListWidth => ShowDetails ? Width - 7 - DetailsWidth : Width - 4;

    public int ListX => 2;
    public int DetailsX => ListX + ListWidth + 3;

    public int BoxTop => 2;
    public int BoxBottom => Height - 2;
    public int HeadingRow => 3;
    public int FirstRow => 4;
    /// <summary>Number of list rows visible.</summary>
    public int VisibleRows => Math.Max(1, BoxBottom - FirstRow);

    // List columns: cursor+mark (2), icon + space (2), name, size (9), gap (2), bar, pct (6), age (2).
    private const int Fixed = 2 + 2 + 9 + 2 + 6 + 2;

    public int BarWidth => ListWidth switch
    {
        >= 70 => Math.Min(28, (ListWidth - Fixed) / 2),
        >= 54 => 10,
        _ => 0,
    };

    public int NameWidth => ListWidth - Fixed - (BarWidth > 0 ? BarWidth + 1 : 0);

    public bool ShowAge => ListWidth >= 50;
}
