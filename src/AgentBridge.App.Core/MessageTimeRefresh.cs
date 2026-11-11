namespace AgentBridge.App.Core;

public sealed class MessageTimeRefresh(MessageStamp stamp)
{
    public string Text { get; private set; } = stamp.Text;

    public TimeSpan? Update(MessageClock clock, bool visible) => null;
}
