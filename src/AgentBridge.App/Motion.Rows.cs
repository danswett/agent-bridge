using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace AgentBridge.App;

public static partial class Motion
{
    internal static RowShift Shift(ListViewBase list, Func<bool> active, Func<bool>? animations = null) =>
        new(list, active, animations ?? (() => Enabled));

    internal sealed class RowShift : IDisposable
    {
        readonly ListViewBase list;
        readonly Func<bool> active, animations;
        readonly HashSet<ListViewItem> shifted = [];
        readonly Dictionary<ListViewItem, Departure> departures = [];
        ContainerVisual? overlay;
        bool listening, disposed;

        internal RowShift(ListViewBase list, Func<bool> active, Func<bool> animations)
        {
            this.list = list; this.active = active; this.animations = animations;
            list.LayoutUpdated += Layout;
            list.ContainerContentChanging += Prepared;
            list.ChoosingItemContainer += Choosing;
            list.Loaded += Loaded;
            list.Unloaded += Unloaded;
            if (list.IsLoaded) { Loaded(list, new RoutedEventArgs()); }
        }

        internal bool Retains(ListViewItem item) => departures.ContainsKey(item);

        void Loaded(object sender, RoutedEventArgs e)
        {
            if (listening) { return; }
            Settings.AnimationsEnabledChanged += AnimationChanged;
            listening = true;
        }
        void Unloaded(object sender, RoutedEventArgs e) { Settings.AnimationsEnabledChanged -= AnimationChanged; listening = false; Clear(); }
        void AnimationChanged(Windows.UI.ViewManagement.UISettings sender, Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs e) =>
            list.DispatcherQueue.TryEnqueue(() => { if (!disposed && list.IsLoaded) { Clear(); Refresh(); } });
        void Layout(object? sender, object e) => Refresh();

        internal void Refresh()
        {
            if (disposed || !active()) { Clear(); return; }
            if (overlay is not null) { overlay.Size = new Vector2((float)list.ActualWidth, (float)list.ActualHeight); }
            if (!animations())
            {
                foreach (var item in shifted.ToArray()) { Detach(item); }
                return;
            }
            if (list.ItemsPanelRoot is not { } panel) { return; }
            foreach (var item in panel.Children.OfType<ListViewItem>())
            {
                if (item.Content is null || !list.Items.Contains(item.Content) || !shifted.Add(item)) { continue; }
                var visual = ElementCompositionPreview.GetElementVisual(item);
                var move = visual.Compositor.CreateVector3KeyFrameAnimation();
                var step = Catalogue.Enter("Shift", true);
                move.Target = "Offset";
                // No starting key: Composition retargets from the currently drawn offset.
                move.InsertExpressionKeyFrame(1, "this.FinalValue", Ease(visual.Compositor, step.Ease));
                move.Duration = step.Duration;
                var implicitAnimations = visual.ImplicitAnimations ?? visual.Compositor.CreateImplicitAnimationCollection();
                implicitAnimations["Offset"] = move;
                visual.ImplicitAnimations = implicitAnimations;
            }
        }

        void Detach(ListViewItem item)
        {
            shifted.Remove(item);
            var visual = ElementCompositionPreview.GetElementVisual(item);
            visual.ImplicitAnimations?.Remove("Offset");
            visual.StopAnimation("Offset");
        }

        void Prepared(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.ItemContainer is not ListViewItem item) { return; }
            if (e.InRecycleQueue) { Detach(item); }
            else if (Retains(item)) { Release(item); }
        }

        void Choosing(ListViewBase sender, ChoosingItemContainerEventArgs e)
        {
            if (e.ItemContainer is ListViewItem item && Retains(item))
            {
                e.ItemContainer = new ListViewItem();
                e.IsContainerPrepared = false;
            }
        }

        // Called before the bound ListView receives the collection notification. The surface
        // keeps the old drawing, not another XAML/automation subtree; its source cannot recycle.
        internal void Changing(IEnumerable<object> removed)
        {
            Refresh();
            if (!active()) { return; }
            foreach (var value in removed)
            {
                if (list.ItemsPanelRoot?.Children.OfType<ListViewItem>().FirstOrDefault(row => ReferenceEquals(row.Content, value)) is not { ContentTemplateRoot: FrameworkElement drawing } item || Retains(item)
                    || drawing.ActualWidth <= 0 || drawing.ActualHeight <= 0) { continue; }
                var origin = drawing.TransformToVisual(list).TransformPoint(default);
                if (origin.Y >= list.ActualHeight || origin.Y + drawing.ActualHeight <= 0) { continue; }
                var departure = new Departure();
                departures.Add(item, departure);
                try
                {
                    // Clearing the container must not empty the live surface's source.
                    departure.Source = drawing;
                    if (drawing.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
                    {
                        drawing.DataContext = value;
                        departure.FrozenContext = true;
                    }
                    if (drawing is SessionRow row) { row.Park(); }
                    var source = ElementCompositionPreview.GetElementVisual(drawing);
                    var c = source.Compositor;
                    if (overlay is null)
                    {
                        overlay = c.CreateContainerVisual();
                        overlay.Size = new Vector2((float)list.ActualWidth, (float)list.ActualHeight);
                        overlay.Clip = c.CreateInsetClip();
                        ElementCompositionPreview.SetElementChildVisual(list, overlay);
                    }
                    departure.Surface = c.CreateVisualSurface();
                    departure.Surface.SourceVisual = source;
                    departure.Surface.SourceSize = new Vector2((float)drawing.ActualWidth, (float)drawing.ActualHeight);
                    departure.Brush = c.CreateSurfaceBrush(departure.Surface);
                    departure.Sprite = c.CreateSpriteVisual();
                    departure.Sprite.Brush = departure.Brush;
                    departure.Sprite.Size = departure.Surface.SourceSize;
                    departure.Sprite.Offset = new Vector3((float)origin.X, (float)origin.Y, 0);
                    overlay.Children.InsertAtTop(departure.Sprite);
                    var step = Catalogue.Exit("ListItem", animations());
                    var fade = c.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0, 1);
                    fade.InsertKeyFrame(1, 0, Ease(c, step.Ease));
                    fade.Duration = step.Duration;
                    departure.Batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
                    departure.Completed = (_, _) => Release(item);
                    departure.Batch.Completed += departure.Completed;
                    departure.Sprite.StartAnimation("Opacity", fade);
                    departure.Batch.End();
                }
                catch { Release(item); throw; }
            }
        }

        void Release(ListViewItem item)
        {
            if (!departures.Remove(item, out var departure)) { return; }
            if (departure.Batch is { } batch) { batch.Completed -= departure.Completed; }
            if (departure.Sprite is { } sprite)
            {
                sprite.StopAnimation("Opacity");
                overlay?.Children.Remove(sprite);
                sprite.Brush = null;
                sprite.Dispose();
            }
            if (departure.Surface is { } surface) { surface.SourceVisual = null; surface.Dispose(); }
            departure.Brush?.Dispose();
            departure.Batch?.Dispose();
            if (departure.FrozenContext) { departure.Source?.ClearValue(FrameworkElement.DataContextProperty); }
            departure.Source = null;
            if (departures.Count == 0 && overlay is { } empty)
            {
                ElementCompositionPreview.SetElementChildVisual(list, null);
                empty.Clip?.Dispose();
                empty.Dispose();
                overlay = null;
            }
        }

        internal void Clear()
        {
            foreach (var item in departures.Keys.ToArray()) { Release(item); }
            foreach (var item in shifted.ToArray()) { Detach(item); }
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

        sealed class Departure
        {
            internal FrameworkElement? Source;
            internal bool FrozenContext;
            internal CompositionVisualSurface? Surface;
            internal CompositionSurfaceBrush? Brush;
            internal SpriteVisual? Sprite;
            internal CompositionScopedBatch? Batch;
            internal TypedEventHandler<object, CompositionBatchCompletedEventArgs>? Completed;
        }
    }
}
