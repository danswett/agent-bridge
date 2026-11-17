using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckRowMotion(MainWindow window, Action<bool, string> check, string outFile)
    {
        var root = (Panel)window.RootElement;
        var design = Application.Current.Resources.MergedDictionaries.First(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        var palette = (ResourceDictionary)design.ThemeDictionaries["Light"];
        var background = (SolidColorBrush)palette["AbCard"];
        var tracked = (SolidColorBrush)palette["AbBridge"];
        var list = new ListView
        {
            Width = 240, Height = 300, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = background, ItemContainerTransitions = new TransitionCollection(),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Border Height="80" Background="{Binding}" />
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
        var first = (SolidColorBrush)palette["AbDanger"];
        var added = (SolidColorBrush)palette["AbAgent"];
        var items = new ObservableCollection<object> { first, tracked };
        var animations = true;
        using var shifts = Motion.Shift(list, () => true, () => animations);
        items.CollectionChanged += (_, _) => shifts.Changing();
        list.ItemsSource = items;
        root.Children.Add(list);
        try
        {
            await CaptureFrame(root);
            await Task.Delay(250);
            var neighbour = (ListViewItem)list.ContainerFromItem(tracked);
            var original = TrackedTop();
            items.Remove(first);
            list.UpdateLayout();
            var slot = LayoutInformation.GetLayoutSlot(neighbour);
            await CaptureFrame(root);
            var moving = TrackedTop();
            check(moving > slot.Y + 1 && moving < original, $"row neighbour is mid-shift ({original}, {moving}, {slot.Y})");
            check(LayoutInformation.GetLayoutSlot(neighbour) == slot, "row shift does not change the layout between frames");
            items.Insert(0, added);
            list.UpdateLayout();
            var target = LayoutInformation.GetLayoutSlot(neighbour).Y;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(10);
            await CaptureFrame(root);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            var later = TrackedTop();
            var progress = (later - moving) / (target - moving);
            var t = Math.Clamp(elapsed / Motion.Catalogue.Enter("Shift", true).Duration.TotalMilliseconds, 0, 1);
            var standard = t * t * (3 - 2 * t);
            check(elapsed < 120 && progress > standard + 0.12,
                $"interrupted shift decelerates toward its new position ({elapsed:0} ms, {progress:0.00} progress)");
            animations = false;
            shifts.Changing();
            items.Remove(added);
            list.UpdateLayout();
            await CaptureFrame(root);
            check(Math.Abs(TrackedTop() - LayoutInformation.GetLayoutSlot(neighbour).Y) <= 1,
                "reduced-motion row neighbours take their final positions without travel");
        }
        finally { root.Children.Remove(list); }

        double TrackedTop()
        {
            var picture = WindowPicture.Take(MainWindow.CurrentHandle, 0);
            for (var y = 0; y < list.ActualHeight; y++)
            {
                var point = list.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(120, y));
                var x = (int)Math.Round(point.X * picture.Width / root.ActualWidth);
                var py = (int)Math.Round(point.Y * picture.Height / root.ActualHeight);
                var at = (py * (int)picture.Width + x) * 4;
                if (Math.Abs(picture.Pixels[at + 2] - tracked.Color.R) <= 2
                    && Math.Abs(picture.Pixels[at + 1] - tracked.Color.G) <= 2
                    && Math.Abs(picture.Pixels[at] - tracked.Color.B) <= 2) { return y; }
            }
            return double.NaN;
        }
    }
}
