using System.Collections.ObjectModel;
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
    static async Task<double> RowMotionSettled(FrameworkElement root, Func<double> read, Action<bool, string> check, string phase)
    {
        const int frames = 8;
        double previous = double.NaN, current = double.NaN;
        for (var frame = 0; frame < frames; frame++)
        {
            previous = current;
            await CaptureFrame(root);
            current = read();
            if (double.IsFinite(previous) && double.IsFinite(current) && Math.Abs(current - previous) <= 1) { return current; }
        }
        check(false, $"{phase} drawn position did not settle after {frames} capture frames ({previous}, {current}).");
        return double.NaN;
    }

    // A retarget needs both RowShift's restamp and a fresh controller: the paused first one stays resolvable until replaced.
    static async Task<AnimationController?> RowMotionController(Visual visual, FrameworkElement root, Action<bool, string> check, string phase, AnimationController? previous = null, Func<bool>? restamped = null)
    {
        var frames = previous is null ? 4 : 8;
        for (var waited = 0; waited <= frames; waited++)
        {
            if (visual.TryGetAnimationController("Offset") is { } controller && !ReferenceEquals(controller, previous) && restamped?.Invoke() != false) { return controller; }
            if (waited < frames) { await CaptureFrame(root); }
        }
        check(false, previous is null ? $"{phase} row shift Offset animation never started while sampled: no controller observed after {frames} capture frames." : $"{phase} found no replacement controller after {frames} capture frames");
        return null;
    }

    // Failure text only: reflection keeps RowShift uninstrumented, and a renamed field throws rather than reading as a default.
    static (bool Tracked, long? Started) RowShiftStart(Motion.RowShift shifts, ListViewItem item)
    {
        var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var rows = (System.Collections.IDictionary)typeof(Motion.RowShift).GetField("rows", hidden)!.GetValue(shifts)!;
        return rows[item] is { } state ? (true, (long?)state.GetType().GetField("Started", hidden)!.GetValue(state)) : (false, null);
    }

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
            await Task.Delay(Motion.Catalogue.Enter("Shift", true).Duration);
            var neighbour = (ListViewItem)list.ContainerFromItem(tracked);
            var original = TrackedTop();
            items.Remove(first);
            list.UpdateLayout();
            var slot = LayoutInformation.GetLayoutSlot(neighbour);
            var initial = await RowMotionController(ElementCompositionPreview.GetElementVisual(neighbour), root, check, "initial");
            if (initial is null) { return; }
            // Commit an interior point before replacing the first animation. Otherwise
            // both layout changes can reach composition together and travel nowhere.
            initial.Pause();
            initial.Progress = 0.25f;
            await CaptureFrame(root);
            check(LayoutInformation.GetLayoutSlot(neighbour) == slot, "row shift does not change the layout between frames");
            var initialStart = RowShiftStart(shifts, neighbour);
            items.Insert(0, added);
            list.UpdateLayout();
            var visual = ElementCompositionPreview.GetElementVisual(neighbour);
            var tops = new List<double>();
            var failure = "";
            Action<bool, string> reportFailure = (ok, text) => failure = ok ? failure : $"{failure}; {text}";
            var retarget = await RowMotionController(visual, root, reportFailure, "retarget", initial,
                () => RowShiftStart(shifts, neighbour).Started is { } stamp && stamp != initialStart.Started);
            if (retarget is null) { check(false, Diagnosis()); return; }
            // A window capture blocks the UI thread, not the compositor. Sample the
            // retarget's own paused start, not an earlier picture of the first shift.
            var moving = await RowMotionSettled(root, () => Sample(0), reportFailure, "retarget start");
            if (!double.IsFinite(moving)) { check(false, Diagnosis()); return; }
            var midShift = moving > slot.Y + 1 && moving < original;
            check(midShift, $"row neighbour is mid-shift ({original}, {moving}, {slot.Y})" + (midShift ? "" : $"; {Diagnosis()}"));
            var target = original;
            var later = await RowMotionSettled(root, () => Sample(0.25f), reportFailure, "retarget quarter progress");
            if (!double.IsFinite(later)) { check(false, Diagnosis()); return; }
            check(later >= moving - 5 && later <= target + 1,
                $"interrupted shift continues from its drawn position ({moving}, {later}, {target})");
            var progress = (later - moving) / (target - moving);
            const double t = 0.25;
            var standard = t * t * (3 - 2 * t);
            check(progress > standard + 0.12,
                $"interrupted shift decelerates toward its new position ({t:0.00} timeline, {progress:0.00} progress)");
            var paused = await RowMotionSettled(root, () => Sample(0), reportFailure, $"retarget rewind (moving={moving}, original={original}, slot.Y={slot.Y})");
            if (!double.IsFinite(paused)) { check(false, Diagnosis()); return; }
            await CaptureFrame(root);
            var held = Sample(0);
            check(retarget?.Progress == 0 && double.IsFinite(paused) && Math.Abs(held - paused) <= 1 && Math.Abs(paused - moving) <= 3,
                $"retarget is paused before motion is disabled ({later}, {target}; moving={moving}, original={original}, slot.Y={slot.Y}, paused={paused}, held={held}, controller={retarget?.Progress.ToString() ?? "none"}); {Diagnosis()}");
            animations = false;
            shifts.Changing();
            await CaptureFrame(root);
            check(Math.Abs(TrackedTop() - target) <= 1,
                "disabling motion settles an interrupted shift without another list change");
            animations = true;
            shifts.Changing();
            var implicitStart = TrackedTop();
            items.Remove(added);
            list.UpdateLayout();
            var reducedSlot = LayoutInformation.GetLayoutSlot(neighbour).Y;
            var implicitMove = double.NaN;
            for (var sample = 0; sample < 4; sample++)
            {
                await CaptureFrame(root);
                implicitMove = TrackedTop();
                if (implicitMove > reducedSlot + 1 && implicitMove < implicitStart - 1) { break; }
            }
            check(implicitMove > reducedSlot + 1 && implicitMove < implicitStart - 1,
                $"row is moving before its implicit shift is disabled ({implicitStart}, {implicitMove}, {reducedSlot})");
            animations = false;
            shifts.Changing();
            await CaptureFrame(root);
            check(Math.Abs(TrackedTop() - reducedSlot) <= 1,
                "disabling motion settles an implicit shift without another list change");
            // Re-resolve every capture and seek: pausing a replaced controller's stale wrapper leaves the row running.
            double Sample(float progress)
            {
                retarget = visual.TryGetAnimationController("Offset");
                if (retarget is null || ReferenceEquals(retarget, initial)) { failure = "no replacement controller during capture"; return double.NaN; }
                retarget.Pause(); retarget.Progress = progress;
                tops.Add(TrackedTop());
                return tops[^1];
            }
            string Diagnosis()
            {
                var current = visual.TryGetAnimationController("Offset");
                return $"retarget diagnosis: {failure}; Offset controllers as managed wrappers, not native ids: sampled is initial={ReferenceEquals(retarget, initial)}, current is sampled={ReferenceEquals(current, retarget)}, current is initial={ReferenceEquals(current, initial)}; "
                    + $"progress initial={initial?.Progress.ToString() ?? "none"} sampled={retarget?.Progress.ToString() ?? "none"} current={current?.Progress.ToString() ?? "none"}; "
                    + $"pixels [{string.Join(", ", tops.TakeLast(8))}]";
            }
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
