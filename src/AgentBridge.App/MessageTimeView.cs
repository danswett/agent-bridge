using AgentBridge.App.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentBridge.App;

public sealed class MessageTimeView : Button
{
    public static readonly DependencyProperty StampProperty = DependencyProperty.Register(
        nameof(Stamp), typeof(MessageStamp), typeof(MessageTimeView), new PropertyMetadata(null, (d, e) =>
        {
            var view = (MessageTimeView)d;
            view.refresh = e.NewValue is MessageStamp stamp ? new MessageTimeRefresh(stamp) : null;
            view.Refresh();
        }));

    MessageTimeRefresh? refresh;
    DispatcherQueueTimer? timer;
    XamlRoot? root;
    Rect viewport;

    public MessageTimeView()
    {
        Loaded += (_, _) =>
        {
            if (root is not null) { root.Changed -= OnRootChanged; }
            root = XamlRoot;
            if (root is not null) { root.Changed += OnRootChanged; }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) { return; }
            timer?.Stop();
            if (root is not null) { root.Changed -= OnRootChanged; root = null; }
        };
        EffectiveViewportChanged += (_, e) =>
        {
            viewport = e.EffectiveViewport;
            if (RefreshPending != Visible()) { Refresh(); }
        };
        SizeChanged += (_, _) => { if (!RefreshPending) { Refresh(); } };
    }

    public MessageStamp? Stamp { get => (MessageStamp?)GetValue(StampProperty); set => SetValue(StampProperty, value); }
    internal Func<MessageClock> ReadClock { get; set; } = () => MessageClock.Local;
    internal bool RefreshPending => timer?.IsRunning == true;

    bool Visible()
    {
        if (!IsLoaded || root?.IsHostVisible != true || viewport.Width <= 0 || viewport.Height <= 0
            || viewport.Right <= 0 || viewport.Bottom <= 0 || viewport.X >= ActualWidth || viewport.Y >= ActualHeight) { return false; }
        for (DependencyObject? up = this; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is UIElement { Visibility: Visibility.Collapsed }) { return false; }
        }
        return true;
    }

    void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();

    internal void Refresh()
    {
        timer?.Stop();
        if (refresh is null || Content is not TextBlock text || refresh.Update(ReadClock(), Visible()) is not { } delay) { return; }
        if (text.Text != refresh.Text) { text.Text = refresh.Text; }
        if (timer is null)
        {
            timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Refresh();
        }
        timer.Interval = delay;
        timer.Start();
    }
}
