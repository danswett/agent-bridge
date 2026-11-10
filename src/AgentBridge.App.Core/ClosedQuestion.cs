using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// A read-only record: a host outcome alone never says whose answer it was.
public sealed class ClosedQuestion(Question question, Delivery? delivery = null) : Observable
{
    public const int Kept = 20;
    bool open;

    public Question Question { get; } = question;
    public bool Approval => Question.Kind == QuestionKind.Approval;
    public string Heading => Approval ? "Permission request" : Question.Agent is { } agent ? $"{HiddenText.Reveal(agent)} asked" : "Main agent asked";
    public string Message => Approval ? ShownText.Reveal(Question.Message.Replace("\r\n", "\n"), rightToLeft: true) : Question.Message;
    public string Summary => delivery is { State: DeliveryState.Done } && QuestionViewModel.OutlivesQuestion(delivery)
        ? QuestionViewModel.Describe(delivery, null).Whole : "Question closed";
    public string Announcement => delivery is { State: DeliveryState.Done, Outcome: "delivered" } ? "Question answered" : "Question closed";

    public bool Open
    {
        get => open;
        set { if (Set(ref open, value)) { Raise(nameof(ToggleText)); } }
    }

    public string ToggleText => Open ? "Hide question" : "Show question";
}

public sealed record HistoryItem(ConversationBlock? Response, ClosedQuestion? Question);
