using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckSelectionBar(MainWindow window, Action<bool, string> check, string outFile)
    {
        var root = (Panel)window.RootElement;
        var fixture = new ListView
        {
            Width = 260,
            Height = 330,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SelectionMode = ListViewSelectionMode.Single,
            SingleSelectionFollowsFocus = false,
            ItemContainerStyle = window.Sessions.Rows.ItemContainerStyle
        };
        var items = new[] { 28, 48, 64, 96 }.Select(height => new ListViewItem
        {
            Height = height,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = $"Row {height}", Margin = new Thickness(24, 0, 0, 0) }
        }).ToArray();
        var unread = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0), Spacing = 8 };
        unread.Children.Add(new TextBlock { Text = "Unread, not selected" });
        unread.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Ui.Brush("AbBridge") });
        items[1].Content = unread;
        foreach (var item in items) { fixture.Items.Add(item); }
        var before = root.RequestedTheme;
        root.Children.Add(fixture);
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(300);
                await Inspect(theme.ToString(), theme == ElementTheme.Dark
                    ? Color.FromArgb(255, 0x2D, 0xD4, 0xBF)
                    : Color.FromArgb(255, 0x0B, 0x7A, 0x6E));
            }
            root.RequestedTheme = ElementTheme.Default;
            var contrast = await HighContrastPass.BeginAsync();
            try
            {
                if (contrast.On)
                {
                    await Task.Delay(1500);
                    if (Application.Current.Resources.TryGetValue("SystemColorHighlightColor", out var value) && value is Color colour)
                    {
                        await Inspect("HighContrast", colour);
                    }
                    else { check(false, "selection bar reads the system highlight colour in high contrast"); }
                }
                else if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                {
                    check(false, $"selection bar forces high contrast on hosted CI ({contrast.How})");
                }
            }
            finally { await contrast.EndAsync(); }
        }
        finally
        {
            root.Children.Remove(fixture);
            root.RequestedTheme = before;
        }

        static bool Near(double a, double b) => Math.Abs(a - b) <= 0.1;
        List<FrameworkElement> Bars(ListViewItem item) => Find<FrameworkElement>(item)
            .Where(e => e is Border or Rectangle && Near(e.ActualWidth, 3) && e.ActualHeight > 0
                && Shown(e, item) && e.Opacity > 0).ToList();

        async Task Inspect(string theme, Color expected)
        {
            for (var index = 0; index < items.Length; index++)
            {
                fixture.SelectedIndex = index;
                fixture.UpdateLayout();
                await CaptureFrame(fixture);
                await Measure(items[index], $"{theme}, row {items[index].Height:0}", expected);
                check(items.Sum(item => Bars(item).Count) == 1,
                    $"selection bar is unique after selection moves in {theme} to row {index}");
            }
            // Reuse the same selected container with new content and a different height.
            var reused = items[^1];
            reused.Content = new TextBlock { Text = "Recycled short row", Margin = new Thickness(40, 0, 0, 0) };
            reused.Height = 28;
            reused.Padding = new Thickness(32, 0, 12, 0);
            fixture.UpdateLayout();
            await CaptureFrame(fixture);
            await Measure(reused, $"{theme}, tall container reused at 28 with wider content padding", expected);
            reused.Height = 96;
            reused.ClearValue(Control.PaddingProperty);
            fixture.UpdateLayout();
            await CaptureFrame(fixture);
            await Measure(reused, $"{theme}, same container grows back to 96", expected);

            var selected = items[^1];
            var selectedFill = await FillPixel(selected);
            foreach (var state in new[] { "PointerOverSelected", "PressedSelected" })
            {
                VisualStateManager.GoToState(selected, state, false);
                fixture.UpdateLayout();
                await CaptureFrame(fixture);
                await Measure(selected, $"{theme}, {state}", expected);
                if (state == "PointerOverSelected")
                {
                    check(await FillPixel(selected) == selectedFill, $"selection bar keeps the selected fill without adding hover fill in {theme}");
                }
            }
            VisualStateManager.GoToState(selected, "Selected", false);
            fixture.SelectedIndex = -1;
            items[0].Focus(FocusState.Keyboard);
            foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
            {
                VisualStateManager.GoToState(items[0], state, false);
                fixture.UpdateLayout();
                await CaptureFrame(fixture);
                check(items.All(item => Bars(item).Count == 0),
                    $"selection bar is absent on unselected focused or {state} rows in {theme}");
            }
            fixture.SelectedIndex = 2;
            fixture.UpdateLayout();
            await CaptureFrame(fixture);
            await Picture(fixture, Path.ChangeExtension(outFile, $".selection-bar.{theme.ToLowerInvariant()}.png"));
        }

        static async Task<uint> FillPixel(ListViewItem item)
        {
            var picture = await PicturePixels(item);
            var x = (int)Math.Round(16 * picture.Width / item.ActualWidth);
            var y = (int)picture.Height / 2;
            return BitConverter.ToUInt32(picture.Pixels, (y * (int)picture.Width + x) * 4);
        }

        async Task Measure(ListViewItem item, string label, Color expected)
        {
            var bars = Bars(item);
            if (bars.Count != 1)
            {
                check(false, $"selection bar has one rendered 3 px bar ({label}; found {bars.Count})");
                return;
            }
            var bar = bars[0];
            var origin = bar.TransformToVisual(item).TransformPoint(default);
            check(Near(bar.ActualWidth, 3) && Near(bar.ActualHeight, item.ActualHeight - 8)
                && Near(origin.X, 4) && Near(origin.Y, 4),
                $"selection bar follows row bounds ({label}; row {item.ActualHeight:0.##}, bar {bar.ActualWidth:0.##}x{bar.ActualHeight:0.##} at {origin.X:0.##},{origin.Y:0.##})");
            var rounded = bar is Border border
                ? border.CornerRadius == new CornerRadius(1.5) && border.BorderThickness == new Thickness(0)
                : bar is Rectangle rectangle && Near(rectangle.RadiusX, 1.5) && Near(rectangle.RadiusY, 1.5);
            check(rounded && bar.Shadow is null, $"selection bar has fully rounded ends, no border or shadow ({label})");
            var brush = bar is Border b ? b.Background : ((Rectangle)bar).Fill;
            check(brush is SolidColorBrush solid && solid.Color == expected,
                $"selection bar uses Bridge or system highlight ({label}; wanted {expected}, saw {(brush as SolidColorBrush)?.Color})");
            var picture = await PicturePixels(bar);
            var x = (int)picture.Width / 2;
            var y = (int)picture.Height / 2;
            var offset = (y * (int)picture.Width + x) * 4;
            var painted = picture.Width > 0 && picture.Height > 0 && picture.Pixels[offset + 3] == 255
                && Math.Abs(picture.Pixels[offset] - expected.B) <= 2
                && Math.Abs(picture.Pixels[offset + 1] - expected.G) <= 2
                && Math.Abs(picture.Pixels[offset + 2] - expected.R) <= 2;
            check(painted, $"selection bar paints its expected colour in captured pixels ({label})");
        }
    }
}
