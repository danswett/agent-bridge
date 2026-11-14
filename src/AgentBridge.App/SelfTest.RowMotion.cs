using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckRowMotion(MainWindow window, Action<bool, string> check)
    {
        var root = (Panel)window.RootElement;
        var design = Application.Current.Resources.MergedDictionaries.First(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        var palette = (ResourceDictionary)design.ThemeDictionaries["Light"];
        var background = (SolidColorBrush)palette["AbCard"];
        var departing = (SolidColorBrush)palette["AbDanger"];
        var following = (SolidColorBrush)palette["AbActive"];
        var tracked = (SolidColorBrush)palette["AbBridge"];
        var inserted = (SolidColorBrush)palette["AbAgent"];
        var list = new ListView
        {
            Width = 240, Height = 240, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = background,
            ItemContainerTransitions = new TransitionCollection(),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Border Height="60" Background="{Binding}" />
                </DataTemplate>
                """),
            ItemContainerStyle = (Style)XamlReader.Load("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
                    <Setter Property="Padding" Value="0" />
                    <Setter Property="MinHeight" Value="0" />
                    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                </Style>
                """)
        };
        var items = new ObservableCollection<object> { departing, following, tracked };
        var animations = true;
        using var shifts = Motion.Shift(list, () => true, () => animations);
        items.CollectionChanged += (_, e) => shifts.Changing(e.Action == NotifyCollectionChangedAction.Remove
            ? e.OldItems!.Cast<object>() : []);
        list.ItemsSource = items;
        root.Children.Add(list);
        try
        {
            await CaptureFrame(root);
            await Task.Delay(250);
            var source = (ListViewItem)list.ContainerFromItem(departing);
            var neighbour = (ListViewItem)list.ContainerFromItem(tracked);
            var original = TrackedTop();
            items.Remove(departing);
            list.UpdateLayout();
            var ghost = (ElementCompositionPreview.GetElementChildVisual(list) as ContainerVisual)?.Children.OfType<SpriteVisual>().SingleOrDefault();
            var fade = ghost?.TryGetAnimationController("Opacity");
            fade?.Pause();
            if (fade is not null) { fade.Progress = 0.5f; }
            source.Content = null;
            var slot = LayoutInformation.GetLayoutSlot(neighbour);
            check(shifts.Retains(source), "row departure retains its source while the drawn-only copy fades");
            items.Add(palette["AbNeeds"]);
            list.UpdateLayout();
            check(!ReferenceEquals(list.ContainerFromItem(items[^1]), source), "row departure never recycles its source into an arriving row");
            await CaptureFrame(root);
            var picture = WindowPicture.Take(MainWindow.CurrentHandle, 0);
            var first = TrackedTop(picture);
            check(first > slot.Y + 1 && first < original, $"row neighbour is mid-shift, not at its final layout position ({original}, {first}, {slot.Y})");
            var point = list.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(120, 10));
            var pixel = Pixel(picture, point);
            check(!Matches(pixel, departing) && !Matches(pixel, background) && !Matches(pixel, following),
                $"departing row is mid-fade where it was ({pixel})");
            check(LayoutInformation.GetLayoutSlot(neighbour) == slot, "row shift changes composition only, not the layout box between frames");
            first = TrackedTop();
            items.Insert(0, inserted);
            list.UpdateLayout();
            var retargetSlot = LayoutInformation.GetLayoutSlot(neighbour);
            await CaptureFrame(root);
            var retarget = TrackedTop();
            check(Math.Abs(retarget - first) < 20 && retarget < retargetSlot.Y - 1,
                $"interrupted row shift starts at its current drawn offset, not the previous target ({first}, {retarget}, {retargetSlot.Y})");
            check(LayoutInformation.GetLayoutSlot(neighbour) == retargetSlot, "retargeted row shift keeps its final layout box fixed");
            fade?.Resume();
            await Task.Delay(250);
            check(!shifts.Retains(source) && ElementCompositionPreview.GetElementChildVisual(list) is null,
                "completed row departures release the source guard, surface and child visual");
            animations = false;
            var cancelled = (ListViewItem)list.ContainerFromItem(inserted);
            items.Remove(inserted);
            list.UpdateLayout();
            var reducedGhost = (ElementCompositionPreview.GetElementChildVisual(list) as ContainerVisual)?.Children.OfType<SpriteVisual>().SingleOrDefault();
            reducedGhost?.TryGetAnimationController("Opacity")?.Pause();
            check(shifts.Retains(cancelled), "reduced-motion departure keeps only its fade, with a guarded source");
            var reducedSlot = LayoutInformation.GetLayoutSlot(neighbour);
            await CaptureFrame(root);
            check(Math.Abs(TrackedTop() - reducedSlot.Y) <= 1, "reduced-motion row neighbours take their final positions without travel");
            shifts.Clear();
            check(!shifts.Retains(cancelled) && ElementCompositionPreview.GetElementChildVisual(list) is null, "cancelling row departures releases every child visual and source guard");
            animations = true;
            var unloaded = (ListViewItem)list.ContainerFromItem(following);
            items.Remove(following);
            list.UpdateLayout();
            check(shifts.Retains(unloaded), "navigation cancels an active guarded departure");
            root.Children.Remove(list);
            await CaptureFrame(root);
            check(!shifts.Retains(unloaded) && ElementCompositionPreview.GetElementChildVisual(list) is null, "navigation unload releases row departure surfaces and child visuals");
        }
        finally { root.Children.Remove(list); }

        static bool Matches((byte R, byte G, byte B) pixel, SolidColorBrush brush) =>
            Math.Abs(pixel.R - brush.Color.R) <= 2 && Math.Abs(pixel.G - brush.Color.G) <= 2 && Math.Abs(pixel.B - brush.Color.B) <= 2;
        (byte R, byte G, byte B) Pixel((byte[] Pixels, uint Width, uint Height, Windows.Graphics.Imaging.BitmapAlphaMode Alpha) picture, Windows.Foundation.Point point)
        {
            var x = (int)Math.Round(point.X * picture.Width / root.ActualWidth);
            var y = (int)Math.Round(point.Y * picture.Height / root.ActualHeight);
            var offset = (y * (int)picture.Width + x) * 4;
            return (picture.Pixels[offset + 2], picture.Pixels[offset + 1], picture.Pixels[offset]);
        }
        double TrackedTop((byte[] Pixels, uint Width, uint Height, Windows.Graphics.Imaging.BitmapAlphaMode Alpha)? captured = null)
        {
            var picture = captured ?? WindowPicture.Take(MainWindow.CurrentHandle, 0);
            for (var y = 0; y < list.ActualHeight; y++)
            {
                var colour = Pixel(picture, list.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(120, y)));
                if (Matches(colour, tracked)) { return y; }
            }
            return double.NaN;
        }
    }
}
