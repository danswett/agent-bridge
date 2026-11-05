namespace AgentBridge.App.Core;

// How two pictures of the conversation's reading column, before a live resize and after it, differ
// (#765). The self-test's proof that the block at the top stayed put is its place in the layout;
// the pictures back that up, so they allow what a renderer varies from one frame to the next and
// nothing a move makes. A pixel differs when a channel is more than Tolerance apart. A few such
// pixels are a renderer's noise (one speck of a card's translucent fill by its rounded corner drawn
// 15 apart in alpha on x64, with nothing else in the picture changed; two text edges 1 apart on
// arm64), so the pictures are alike unless more than MostDiffering pixels differ or any
// pixel differs by more than Glaring. A block moved by even a pixel differs by far more, along
// every edge of its text.
public readonly record struct PictureDifference(int Differing, int Largest)
{
    public const int Tolerance = 8;
    public const int MostDiffering = 4;
    public const int Glaring = 96;

    public bool Alike => Differing <= MostDiffering && Largest <= Glaring;

    // Two pictures of width × height, 4 bytes a pixel, compared in the columns fromX to toX.
    public static PictureDifference Of(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int width, int height, int fromX, int toX)
    {
        var (differing, largest) = (0, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = Math.Max(0, fromX); x < Math.Min(width, toX); x++)
            {
                var i = (y * width + x) * 4;
                var most = 0;
                for (var c = 0; c < 4; c++) { most = Math.Max(most, Math.Abs(after[i + c] - before[i + c])); }
                largest = Math.Max(largest, most);
                if (most > Tolerance) { differing++; }
            }
        }
        return new(differing, largest);
    }
}
