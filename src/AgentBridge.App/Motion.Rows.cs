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
        bool listening, disposed;

        internal RowShift(ListViewBase list, Func<bool> active, Func<bool> animations)
        {
            this.list = list;
            this.active = active;
            this.animations = animations;
            list.LayoutUpdated += Layout;
            list.ContainerContentChanging += Prepared;
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
            if (disposed || !active() || !animations()) { Clear(); return; }
            if (list.ItemsPanelRoot is not { } panel) { return; }
            foreach (var item in rows.Keys.ToArray())
            {
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
                        visual.StartAnimation("Offset", move);
                    }
                    state.Offset = visual.Offset;
                    state.Started = Stopwatch.GetTimestamp();
                }
            }
        }

        internal void Changing() => Refresh();

        static void Configure(Visual visual, MotionEase ease)
        {
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.Target = "Offset";
            animation.InsertExpressionKeyFrame(1, "this.FinalValue", Ease(visual.Compositor, ease));
            animation.Duration = Catalogue.Enter("Shift", true).Duration;
            var implicitAnimations = visual.ImplicitAnimations ?? visual.Compositor.CreateImplicitAnimationCollection();
            implicitAnimations["Offset"] = animation;
            visual.ImplicitAnimations = implicitAnimations;
        }

        void Prepared(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.InRecycleQueue && e.ItemContainer is ListViewItem item) { Detach(item); }
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
            foreach (var item in rows.Keys.ToArray()) { Detach(item); }
        }

        public void Dispose()
        {
            disposed = true;
            Clear();
            Settings.AnimationsEnabledChanged -= AnimationChanged;
            list.LayoutUpdated -= Layout;
            list.ContainerContentChanging -= Prepared;
            list.Loaded -= Loaded;
            list.Unloaded -= Unloaded;
        }

        sealed class Shifted(object item, Vector3 offset)
        {
            internal object Item { get; } = item;
            internal Vector3 Offset = offset;
            internal long? Started;
        }
    }
}
