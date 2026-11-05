using AgentBridge.App.Core;
using Xunit;

// The self-test's before and after pictures of the conversation through a live resize (#765): they
// allow the anti-aliasing a renderer varies between two renders, never a block moved.
public sealed class PictureDifferenceTests
{
    const int Width = 60, Height = 30;

    // A column of "text": transparent, with dark strokes of 2 px every 5 px on its middle rows, and a
    // card's translucent fill by its rounded corner, a pixel at alpha 8, at (50, 25).
    static byte[] Column(int shift = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 10; y < 20; y++)
        {
            for (var x = 5; x < 45; x++)
            {
                if ((x - shift) % 5 < 2) { pixels[(y * Width + x) * 4 + 3] = 255; }
            }
        }
        pixels[(25 * Width + 50) * 4 + 3] = 8;
        return pixels;
    }

    static PictureDifference Compare(byte[] before, byte[] after) => PictureDifference.Of(before, after, Width, Height, 0, Width);

    [Fact]
    public void One_pixel_by_a_card_corner_drawn_15_apart_in_alpha_is_renderer_noise_and_the_pictures_are_alike()
    {
        var after = Column();
        after[(25 * Width + 50) * 4 + 3] = 23;

        var difference = Compare(Column(), after);

        Assert.Equal(new PictureDifference(1, 15), difference);
        Assert.True(difference.Alike);
    }

    [Fact]
    public void A_column_moved_by_one_pixel_is_not_alike()
    {
        var difference = Compare(Column(), Column(shift: 1));

        Assert.True(difference.Differing > PictureDifference.MostDiffering);
        Assert.Equal(255, difference.Largest);
        Assert.False(difference.Alike);
    }

    [Fact]
    public void Five_pixels_drawn_9_apart_are_more_than_anti_aliasing()
    {
        var after = Column();
        for (var x = 0; x < 5; x++) { after[(2 * Width + x) * 4] = 9; }

        var difference = Compare(Column(), after);

        Assert.Equal(new PictureDifference(5, 9), difference);
        Assert.False(difference.Alike);
    }

    [Fact]
    public void One_pixel_drawn_more_than_96_apart_is_not_alike()
    {
        var after = Column();
        after[(2 * Width + 2) * 4 + 1] = 97;

        Assert.False(Compare(Column(), after).Alike);
    }
}
