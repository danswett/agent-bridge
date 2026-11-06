namespace AgentBridge.App.Core;

// Which of an agent row's statuses Settings raises for Narrator. Each raise is queued for when its
// row is drawn, by which time the setup may have moved on: a raise whose status was superseded, or
// that would say again what was just said, is skipped, so a setup that ends before its progress is
// drawn is heard once, with its result (#705 review).
public sealed class AgentAnnouncements
{
    readonly Dictionary<string, string> said = new(StringComparer.Ordinal);

    // A new setup on this row: its result is news even if the last one said the same.
    public void Begin(string id) => said.Remove(id);

    // Whether to raise the row's status now drawn, for the raise queued when it read `expected`.
    public bool Take(string id, string expected, string drawn)
    {
        if (drawn.Length == 0 || drawn != expected || said.GetValueOrDefault(id) == drawn) { return false; }
        said[id] = drawn;
        return true;
    }
}
