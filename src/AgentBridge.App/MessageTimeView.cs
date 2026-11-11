using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed class MessageTimeView : Button
{
    public static readonly DependencyProperty StampProperty = DependencyProperty.Register(
        nameof(Stamp), typeof(MessageStamp), typeof(MessageTimeView), new PropertyMetadata(null));

    public MessageStamp? Stamp { get => (MessageStamp?)GetValue(StampProperty); set => SetValue(StampProperty, value); }
    internal Func<MessageClock> ReadClock { get; set; } = () => MessageClock.Local;
    internal bool RefreshPending => false;
    internal void Refresh() { }
}
