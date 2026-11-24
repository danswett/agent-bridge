namespace AgentBridge.App.Core;

// A group's header in the Sessions list (ui.md Groups): its name and count, how many of its rows need you,
// and whether it shows its rows. Set: what the header tells when pressed (the group's id and the new state).
public sealed record GroupHeader(string Id, string Name, int Count, int Needs, bool Expanded, Action<string, bool>? Set = null)
{
    public bool Empty => Count == 0;
    public string Text => $"{Name} · {Count}" + (Needs > 0 ? $" · {Needs} need you" : "");
    public string Note => "Nothing here yet. Open a session's menu and choose Move to group.";
    public float Angle => Expanded ? 90f : 0f;
    public void Toggle() => Set?.Invoke(Id, !Expanded);
}
