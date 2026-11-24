namespace AgentBridge.Protocol;

// Clear and send (#804): a person's device clears what is typed in a session's input box, then sends its reply. The device
// signs a command naming the box it read (ComposerInput.Digest); only a person's device this machine approved is taken, and
// only through the relay (the same checks as an escaped approval, EscapedApproval).
public static class ReplyClear
{
    // Offered in hello to a device that could succeed, while the policy is confirm. Advisory: every reply is checked again.
    public const string Capability = "sessions.replyClear";

    // The confirmation's signed-command action: distinct from anything else a device signs.
    public const string Action = "session.reply:clear";

    // replies.clearAndSend. Off unless set: nothing clears a box from another device.
    public const string Confirm = "confirm";
    public const string Off = "off";
}
