using System.Text.Json;

namespace AgentBridge.App.Core;

// A rectangle in physical pixels, as Windows places windows.
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public int Overlap(PixelRect o) =>
        Math.Max(0, Math.Min(Right, o.Right) - Math.Max(X, o.X)) * Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y));
}

// One display: its work area (the screen less the taskbar) and its scale (1.5 at 150 %).
public readonly record struct Display(PixelRect WorkArea, double Scale);

// Where the window was when it was last moved or sized, kept beside the app's settings
// (<profile>-window.json), and where it opens next time (#180): the window resizes freely down
// to layout.windows' smallest size, and comes back where it was unless that is off every screen.
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized = false)
{
    // The first start's size, in effective pixels: wide enough for three panes (ui.md §5.2).
    public const double FirstWidth = DesignLayout.ThreePane;
    public const double FirstHeight = 860;
    // How much of the title bar must be on a screen for the window to stay put: enough to grab.
    const double Grab = 96;

    public PixelRect Rect => new(X, Y, Width, Height);

    public static WindowPlacement? Load(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) { return null; }
            var p = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            return p is { Width: > 0, Height: > 0 } ? p : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }

    // The smallest window on a display, in its physical pixels.
    public static (int Width, int Height) Smallest(double scale) =>
        ((int)Math.Ceiling(DesignLayout.MinWidth * scale), (int)Math.Ceiling(DesignLayout.MinHeight * scale));

    // Where the window opens: where it was, if enough of its title bar is on a display; otherwise
    // centred on the primary display at the first start's size. Never larger than the display it
    // opens on, nor smaller than the smallest window.
    public static WindowPlacement Place(WindowPlacement? saved, IReadOnlyList<Display> displays, Display primary)
    {
        if (saved is not null)
        {
            var strip = new PixelRect(saved.X, saved.Y, saved.Width, (int)Math.Ceiling(DesignLayout.TitleBar * primary.Scale));
            var on = displays.Where(d => d.WorkArea.Overlap(strip) > 0).OrderByDescending(d => d.WorkArea.Overlap(strip)).FirstOrDefault();
            var grabbable = on.WorkArea.Width > 0 && Math.Min(strip.Right, on.WorkArea.Right) - Math.Max(strip.X, on.WorkArea.X) >= Grab * on.Scale && strip.Y >= on.WorkArea.Y;
            if (grabbable) { return Fit(saved, on); }
        }
        var w = (int)Math.Round(FirstWidth * primary.Scale);
        var h = (int)Math.Round(FirstHeight * primary.Scale);
        var area = primary.WorkArea;
        var first = new WindowPlacement(area.X + (area.Width - Math.Min(w, area.Width)) / 2, area.Y + (area.Height - Math.Min(h, area.Height)) / 2, w, h, saved?.Maximized ?? false);
        return Fit(first, primary);
    }

    static WindowPlacement Fit(WindowPlacement p, Display d)
    {
        var (minW, minH) = Smallest(d.Scale);
        var a = d.WorkArea;
        var w = Math.Max(minW, Math.Min(p.Width, a.Width));
        var h = Math.Max(minH, Math.Min(p.Height, a.Height));
        // Pulled back onto the display where it would hang off its right or bottom edge.
        var x = Math.Max(a.X, Math.Min(p.X, a.Right - w));
        var y = Math.Max(a.Y, Math.Min(p.Y, a.Bottom - h));
        return p with { X = x, Y = y, Width = w, Height = h };
    }
}
