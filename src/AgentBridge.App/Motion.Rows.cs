using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using AgentBridge.App.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace AgentBridge.App;

public static partial class Motion
{
    internal static RowShift Shift(ListViewBase list, Func<bool> active, Func<bool>? animations = null) =>
        new(list, active, animations ?? (() => Enabled));

    internal sealed class RowShift : IDisposable
    {
        readonly ListViewBase list;
        readonly Func<bool> active, animations;
        readonly Dictionary<ListViewItem, Shifted> rows = [];
        readonly Dictionary<ListViewItem, Departure> departures = [];
        ContainerVisual? overlay;
        bool listening, disposed;

        internal RowShift(ListViewBase list, Func<bool> active, Func<bool> animations)
        {
            this.list = list;
            this.active = active;
            this.animations = animations;
            list.LayoutUpdated += Layout;
            list.ContainerContentChanging += Prepared;
            list.ChoosingItemContainer += Choosing;
            list.Loaded += Loaded;
            list.Unloaded += Unloaded;
            if (list.IsLoaded) { Loaded(list, new RoutedEventArgs()); }
        }

        void Loaded(object sender, RoutedEventArgs e)
        {
            if (listening) { return; }
            Settings.AnimationsEnabledChanged += AnimationChanged;
            listening = true;
        }

        void Unloaded(object sender, RoutedEventArgs e)
        {
            Settings.AnimationsEnabledChanged -= AnimationChanged;
            listening = false;
            Clear();
        }

        void AnimationChanged(Windows.UI.ViewManagement.UISettings sender, Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs e) =>
            list.DispatcherQueue.TryEnqueue(() => { if (!disposed && list.IsLoaded) { Clear(); Refresh(); } });

        void Layout(object? sender, object e) => Refresh();

        internal void Refresh()
        {
            if (disposed || !active()) { Clear(); return; }
            if (overlay is not null) { overlay.Size = new Vector2((float)list.ActualWidth, (float)list.ActualHeight); }
            if (!animations()) { foreach (var item in rows.Keys.ToArray()) { if (!departures.ContainsKey(item)) { Detach(item); } } return; }
            if (list.ItemsPanelRoot is not { } panel) { return; }
            foreach (var item in rows.Keys.ToArray())
            {
                if (departures.ContainsKey(item)) { continue; }
                if (!panel.Children.Contains(item) || item.Content is null || !list.Items.Contains(item.Content)) { Detach(item); }
            }
            foreach (var item in panel.Children.OfType<ListViewItem>())
            {
                if (item.Content is not { } value || !list.Items.Contains(value)) { continue; }
                var visual = ElementCompositionPreview.GetElementVisual(item);
                if (!rows.TryGetValue(item, out var state))
                {
                    rows.Add(item, new Shifted(value, visual.Offset));
                    Configure(visual, Catalogue.Enter("Shift", true).Ease);
                }
                else if (!ReferenceEquals(value, state.Item))
                {
                    Detach(item);
                    rows.Add(item, new Shifted(value, visual.Offset));
                    Configure(visual, Catalogue.Enter("Shift", true).Ease);
                }
                else if (state.Offset != visual.Offset)
                {
                    var step = Catalogue.Enter("Shift", true);
                    if (state.Started is { } started && Stopwatch.GetElapsedTime(started) < step.Duration)
                    {
                        var decelerate = MotionEase.Parse(Convert.ToString(Application.Current.Resources["AbMotionEaseDecelerate"], CultureInfo.InvariantCulture)!);
                        var move = visual.Compositor.CreateVector3KeyFrameAnimation();
                        // No starting key: replacing the implicit move starts from its drawn offset.
                        move.InsertKeyFrame(1, visual.Offset, Ease(visual.Compositor, decelerate));
                        move.Duration = step.Duration;
                        move.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                        visual.StartAnimation("Offset", move);
                    }
                    state.Offset = visual.Offset;
                    state.Started = Stopwatch.GetTimestamp();
                }
            }
        }

        internal void Changing(IEnumerable<object> removed)
        {
            if (disposed || !active()) { Clear(); return; }
            foreach (var value in removed)
            {
                if (list.ItemsPanelRoot?.Children.OfType<ListViewItem>().FirstOrDefault(row => ReferenceEquals(row.Content, value))
                    is not { ContentTemplateRoot: FrameworkElement drawing } item || departures.ContainsKey(item)
                    || drawing.ActualWidth <= 0 || drawing.ActualHeight <= 0) { continue; }
                var origin = drawing.TransformToVisual(list).TransformPoint(default);
                var departure = new Departure { Source = drawing };
                departures.Add(item, departure);
                try
                {
                    if (drawing.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
                    {
                        drawing.DataContext = value;
                        departure.FrozenContext = true;
                    }
                    if (drawing is SessionRow row) { row.Park(); }
                    var source = ElementCompositionPreview.GetElementVisual(drawing);
                    var compositor = source.Compositor;
                    if (overlay is null)
                    {
                        overlay = compositor.CreateContainerVisual();
                        overlay.Size = new Vector2((float)list.ActualWidth, (float)list.ActualHeight);
                        overlay.Clip = compositor.CreateInsetClip();
                        ElementCompositionPreview.SetElementChildVisual(list, overlay);
                    }
                    departure.Surface = compositor.CreateVisualSurface();
                    departure.Surface.SourceVisual = source;
                    departure.Surface.SourceSize = new Vector2((float)drawing.ActualWidth, (float)drawing.ActualHeight);
                    departure.Brush = compositor.CreateSurfaceBrush(departure.Surface);
                    departure.Sprite = compositor.CreateSpriteVisual();
                    departure.Sprite.Brush = departure.Brush;
                    departure.Sprite.Size = departure.Surface.SourceSize;
                    overlay.Children.InsertAtTop(departure.Sprite);
                    var step = Catalogue.Exit("ListItem", animations());
                    var rowVisual = ElementCompositionPreview.GetElementVisual(item);
                    var position = compositor.CreateVector3KeyFrameAnimation();
                    position.SetReferenceParameter("row", rowVisual);
                    position.SetVector3Parameter("relative", new Vector3((float)origin.X, (float)origin.Y, 0) - rowVisual.Offset);
                    // Expression keyframes are sampled once by the compositor, including the in-flight shift.
                    position.InsertExpressionKeyFrame(0, "row.Offset + relative");
                    position.InsertExpressionKeyFrame(1, "row.Offset + relative");
                    position.Duration = step.Duration;
                    var fade = compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0, 1);
                    fade.InsertKeyFrame(1, 0, Ease(compositor, step.Ease));
                    fade.Duration = step.Duration;
                    departure.Batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                    departure.Batch.Completed += (_, _) => Release(item);
                    departure.Sprite.StartAnimation("Offset", position);
                    departure.Sprite.StartAnimation("Opacity", fade);
                    departure.Batch.End();
                }
                catch { Release(item); throw; }
            }
            Refresh();
        }

        void Choosing(ListViewBase sender, ChoosingItemContainerEventArgs e)
        {
            if (e.ItemContainer is ListViewItem item && departures.ContainsKey(item))
            {
                e.ItemContainer = new ListViewItem();
                e.IsContainerPrepared = false;
            }
        }

        void Release(ListViewItem item)
        {
            if (!departures.Remove(item, out var departure)) { return; }
            Detach(item);
            if (departure.Sprite is { } sprite)
            {
                sprite.StopAnimation("Opacity");
                if (overlay?.Children.Contains(sprite) == true) { overlay.Children.Remove(sprite); }
                sprite.Brush = null;
                sprite.Dispose();
            }
            if (departure.Surface is { } surface) { surface.SourceVisual = null; surface.Dispose(); }
            departure.Brush?.Dispose();
            departure.Batch?.Dispose();
            if (departure.FrozenContext) { departure.Source?.ClearValue(FrameworkElement.DataContextProperty); }
            if (departures.Count == 0 && overlay is { } empty)
            {
                ElementCompositionPreview.SetElementChildVisual(list, null);
                empty.Clip?.Dispose();
                empty.Dispose();
                overlay = null;
            }
        }

        static void Configure(Visual visual, MotionEase ease)
        {
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.Target = "Offset";
            animation.InsertExpressionKeyFrame(1, "this.FinalValue", Ease(visual.Compositor, ease));
            animation.Duration = Catalogue.Enter("Shift", true).Duration;
            animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            var implicitAnimations = visual.ImplicitAnimations ?? visual.Compositor.CreateImplicitAnimationCollection();
            implicitAnimations["Offset"] = animation;
            visual.ImplicitAnimations = implicitAnimations;
        }

        void Prepared(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.InRecycleQueue && e.ItemContainer is ListViewItem item && !departures.ContainsKey(item)) { Detach(item); }
            else if (!e.InRecycleQueue && e.ItemContainer is ListViewItem prepared && departures.ContainsKey(prepared)) { Release(prepared); }
        }

        void Detach(ListViewItem item)
        {
            if (!rows.Remove(item)) { return; }
            var visual = ElementCompositionPreview.GetElementVisual(item);
            visual.ImplicitAnimations?.Remove("Offset");
            visual.StopAnimation("Offset");
        }

        internal void Clear()
        {
            foreach (var item in departures.Keys.ToArray()) { Release(item); }
            foreach (var item in rows.Keys.ToArray()) { Detach(item); }
        }

        public void Dispose()
        {
            disposed = true;
            Clear();
            Settings.AnimationsEnabledChanged -= AnimationChanged;
            list.LayoutUpdated -= Layout;
            list.ContainerContentChanging -= Prepared;
            list.ChoosingItemContainer -= Choosing;
            list.Loaded -= Loaded;
            list.Unloaded -= Unloaded;
        }

        sealed class Shifted(object item, Vector3 offset)
        {
            internal object Item { get; } = item;
            internal Vector3 Offset = offset;
            internal long? Started;
        }

        sealed class Departure
        {
            internal FrameworkElement? Source;
            internal bool FrozenContext;
            internal CompositionVisualSurface? Surface;
            internal CompositionSurfaceBrush? Brush;
            internal SpriteVisual? Sprite;
            internal CompositionScopedBatch? Batch;
        }
    }
}
