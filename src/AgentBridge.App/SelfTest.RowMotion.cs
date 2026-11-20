using System.Collections.ObjectModel;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AgentBridge.App;

static partial class SelfTest
{
    public static async Task RunMoveDownProbeAsync(MainWindow window, string outFile, Action<int> exit)
    {
        var passed = new List<string>();
        var failed = new List<string>();
        void Check(bool ok, string what)
        {
            (ok ? passed : failed).Add(what);
            if (!ok) { AppLog.Write($"self-test: failed: {what}; {ForegroundOwner()}"); }
        }
        var list = window.Sessions.Rows;
        try
        {
            if (!await Until(() => Rows(window).Any(r => r.Card?.Row?.CanMoveDown == true
                && list.ContainerFromItem(r.Card) is ListViewItem item
                && ElementCompositionPreview.GetElementVisual(item).ImplicitAnimations?.ContainsKey("Offset") == true),
                TimeSpan.FromSeconds(10))) { throw new InvalidOperationException("No movable demo row with row-shift motion ready."); }
            Check(Motion.Enabled, "MoveDown probe runs with animation effects enabled");
            foreach (var menu in new[] { false, true })
            {
                await Task.Delay(300);
                var row = Rows(window).First(r => r.Card?.Row?.CanMoveDown == true
                    && list.ContainerFromItem(r.Card) is ListViewItem { ContentTemplateRoot: SessionRow drawn } && drawn == r);
                var card = row.Card!;
                var before = list.Items.IndexOf(card);
                if (menu)
                {
                    row.OpenMenu();
                    await CaptureFrame(window.RootElement);
                    var move = (MenuFlyoutItem)row.FindName("MoveDownItem");
                    ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(move).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                else { row.MoveBy(1); } // The Alt+Down handler's shared entry point.
                var observed = new TaskCompletionSource<(bool Offset, bool Translation)>();
                var paused = new List<AnimationController>();
                void Sample(object? sender, object args)
                {
                    if (observed.Task.IsCompleted || list.Items.IndexOf(card) != before + 1
                        || list.ContainerFromItem(card) is not ListViewItem item || !ReferenceEquals(item.Content, card)) { return; }
                    var visual = ElementCompositionPreview.GetElementVisual(item);
                    var offset = visual.TryGetAnimationController("Offset");
                    var translation = visual.TryGetAnimationController("Translation");
                    if (offset is null && translation is null) { return; }
                    foreach (var controller in new[] { offset, translation }.OfType<AnimationController>())
                    {
                        controller.Pause();
                        paused.Add(controller);
                    }
                    observed.TrySetResult((offset is not null, translation is not null));
                }
                // Subscribe after MoveBy's AfterLayout callback, so its explicit slide is included.
                list.LayoutUpdated += Sample;
                try
                {
                    list.UpdateLayout();
                    var active = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var count = (active.Offset ? 1 : 0) + (active.Translation ? 1 : 0);
                    Check(count == 1, $"{(menu ? "menu" : "keyboard")} MoveDown surviving row has exactly one active offset animation " +
                        $"(count={count}, Offset={active.Offset}, Translation={active.Translation})");
                }
                finally
                {
                    list.LayoutUpdated -= Sample;
                    foreach (var controller in paused) { controller.Resume(); }
                }
            }
        }
        catch (Exception e) { Check(false, $"MoveDown probe crashed: {e}"); }
        await Write(outFile, new Dictionary<string, object?>(), passed, failed, null);
        exit(failed.Count == 0 ? 0 : 1);
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
            await Task.Delay(250);
            var neighbour = (ListViewItem)list.ContainerFromItem(tracked);
            var original = TrackedTop();
            items.Remove(first);
            list.UpdateLayout();
            var slot = LayoutInformation.GetLayoutSlot(neighbour);
            await CaptureFrame(root);
            check(LayoutInformation.GetLayoutSlot(neighbour) == slot, "row shift does not change the layout between frames");
            items.Insert(0, added);
            list.UpdateLayout();
            var retarget = ElementCompositionPreview.GetElementVisual(neighbour).TryGetAnimationController("Offset")
                ?? throw new InvalidOperationException("The interrupted row shift has no Offset animation controller.");
            // A window capture blocks the UI thread, not the compositor. Sample the
            // retarget's own paused start, not an earlier picture of the first shift.
            retarget.Pause();
            retarget.Progress = 0;
            await CaptureFrame(root);
            var moving = TrackedTop();
            check(moving > slot.Y + 1 && moving < original, $"row neighbour is mid-shift ({original}, {moving}, {slot.Y})");
            var target = original;
            retarget.Progress = 0.25f;
            await CaptureFrame(root);
            var later = TrackedTop();
            check(later >= moving - 5 && later <= target + 1,
                $"interrupted shift continues from its drawn position ({moving}, {later}, {target})");
            var progress = (later - moving) / (target - moving);
            const double t = 0.25;
            var standard = t * t * (3 - 2 * t);
            check(progress > standard + 0.12,
                $"interrupted shift decelerates toward its new position ({t:0.00} timeline, {progress:0.00} progress)");
            check(Math.Abs(target - later) > 1,
                $"retarget is paused before motion is disabled ({later}, {target})");
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
