namespace AgentBridge.App.Core;

// The "Question waiting" bar above the reply box (#1657): it shows while a question is open and its
// card is wholly out of the conversation's view, so the person never has to hunt for it. The card
// itself sits in the conversation where it was asked and is never pinned over it.
public static class QuestionBar
{
    // top is the card's offset from the top of the viewport (negative once scrolled past).
    public static bool Shows(bool asked, bool viewingConversation, double top, double height, double viewport)
        => asked && viewingConversation && viewport > 0 && (top + height <= 0 || top >= viewport);
}
