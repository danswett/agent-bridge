namespace AgentBridge.App.Core;

// The words and states of the machine tile (#1147, ui.md §6.6.1): Header, Health, Can run,
// Running, Actions, from existing fields only. What is not reported yet says so in words, never 0.
public static class MachineTile
{
    public const double WideFrom = 900;

    public readonly record struct HealthLine(string Text, string Icon);

    // Shape-distinct icons (Segoe Fluent): a gauge for not measured, a plug for offline.
    public static HealthLine Health(bool offline, string? lastSeen) =>
        offline ? new($"Offline{(lastSeen is null ? "" : $" · last seen {lastSeen}")}", "\uEB55") : new("Not measured yet", "\uE9D9");

    public static string Running(int count) => count == 0 ? "No sessions running" : Words.Plural(count, "session", "sessions");

    public static bool CanLaunchHere(string status, bool viewOnly, bool held) => status is "Online" or "This machine" && !viewOnly && !held;

    public static string? LaunchWhy(string status, bool viewOnly, bool held) =>
        status == "Offline" ? "Offline: it can't start sessions now."
        : viewOnly ? "View only: it takes nothing from other devices."
        : held ? "Discovery held: it can't start sessions now."
        : null;

    public static string Fleet(int total, int online) => $"{Words.Plural(total, "machine", "machines")} · {online} online";

    public static bool IsWide(double width) => width >= WideFrom;
}
