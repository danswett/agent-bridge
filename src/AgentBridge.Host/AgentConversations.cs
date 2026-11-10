namespace AgentBridge.Host;

// One agent conversation as a sending session sees it: who started it, when, how many agent
// messages it has carried, and the distinct sessions it has reached (design 5.4, `via`).
public sealed record AgentConversation(string Root, DateTimeOffset RootAt, int Messages, IReadOnlyList<string> Via);

// The verdict of a depth or budget check. A refusal carries a reason code and no conversation.
public readonly record struct AgentConversationVerdict(bool Allowed, string? Reason, AgentConversation? Conversation)
{
    public const string ChainTooDeep = "chain-too-deep";
    public const string ConversationBudget = "conversation-budget";
    public const string LaunchNotAllowed = "launch-not-allowed";

    internal static AgentConversationVerdict Refuse(string reason) => new(false, reason, null);
}

// What drove a session's latest turn. Only a person resets the chain: approving a tool or a
// permission prompt is not person input, or an agent could keep a conversation alive by asking
// for approvals (design 5.4).
public enum AgentTurnDriver { Person, Approval }

// The local conversation, depth and budget rules (#720 S4b, design 5.4). Pure and unwired, for
// local targets only (no signed chain: that is AP5). State is in memory, so a restart starts every
// session fresh. Session ids are opaque and are never logged or returned in a refusal. A refused
// check changes nothing, so a refusal consumes no budget.
public sealed class AgentConversations
{
    readonly Func<DateTimeOffset> clock;
    readonly int maxDepth, maxMessages;
    readonly TimeSpan maxAge;
    readonly bool allowAgentLaunchedToLaunch;
    readonly object gate = new();
    // The conversation that drove each session's current turn; absent means a person drove it.
    readonly Dictionary<string, AgentConversation> driving = [];
    // Sessions an agent launched. Both tables are capped: see MakeRoom.
    readonly HashSet<string> launchedByAgent = [];
    const int MaxEntries = 1024;

    public AgentConversations(
        Func<DateTimeOffset>? clock = null,
        int maxDepth = 3,
        int conversationMessages = 20,
        int conversationMinutes = 60,
        bool allowAgentLaunchedToLaunch = false)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.maxDepth = maxDepth;
        maxMessages = conversationMessages;
        maxAge = TimeSpan.FromMinutes(conversationMinutes);
        this.allowAgentLaunchedToLaunch = allowAgentLaunchedToLaunch;
    }

    // The host ends a session: forget everything held about it.
    public void SessionEnded(string session)
    {
        lock (gate) { driving.Remove(session); launchedByAgent.Remove(session); }
    }

    // Person input to a session ends its part in any conversation; its next message is a fresh
    // root. An approval changes nothing. A session an agent launched stays marked as launched.
    public void RecordTurn(string session, AgentTurnDriver driver)
    {
        if (driver != AgentTurnDriver.Person) { return; }
        lock (gate) { driving.Remove(session); }
    }

    // `origin` sends an agent message to the existing session `target`.
    public AgentConversationVerdict TrySend(string origin, string target) => Try(origin, target, launch: false);

    // `origin` launches the new session `launched`: a hop to a new session like any other.
    public AgentConversationVerdict TryLaunch(string origin, string launched) => Try(origin, launched, launch: true);

    AgentConversationVerdict Try(string origin, string target, bool launch)
    {
        lock (gate)
        {
            var now = clock();
            if (launch && !allowAgentLaunchedToLaunch && launchedByAgent.Contains(origin))
            {
                return AgentConversationVerdict.Refuse(AgentConversationVerdict.LaunchNotAllowed);
            }

            var current = driving.GetValueOrDefault(origin);
            if (current is not null && now - current.RootAt > maxAge) { current = null; }
            var expired = current is null && driving.ContainsKey(origin);
            // An expired conversation ends the same way as a spent budget; it does not quietly restart.
            if (expired || (current is not null && current.Messages >= maxMessages))
            {
                return AgentConversationVerdict.Refuse(AgentConversationVerdict.ConversationBudget);
            }

            var via = current?.Via.ToList() ?? [];
            if (!via.Contains(origin)) { via.Add(origin); }
            var depth = via.Contains(target) ? via.Count : via.Count + 1;
            if (depth > maxDepth) { return AgentConversationVerdict.Refuse(AgentConversationVerdict.ChainTooDeep); }

            if (!MakeRoom(target, launch))
            {
                return AgentConversationVerdict.Refuse(launch ? AgentConversationVerdict.LaunchNotAllowed : AgentConversationVerdict.ConversationBudget);
            }
            var next = new AgentConversation(current?.Root ?? Guid.NewGuid().ToString("N"), current?.RootAt ?? now, (current?.Messages ?? 0) + 1, via);
            driving[target] = next;
            if (launch) { launchedByAgent.Add(target); }
            return new AgentConversationVerdict(true, null, next);
        }
    }

    // Entries leave only on SessionEnded or a person turn, never by age: an aged-out mark would let
    // a running agent-launched session launch again, and an aged-out conversation would let its
    // session restart with a fresh budget and no person input. A full table refuses the new entry.
    bool MakeRoom(string target, bool launch)
    {
        if (!driving.ContainsKey(target) && driving.Count >= MaxEntries) { return false; }
        return !(launch && !launchedByAgent.Contains(target) && launchedByAgent.Count >= MaxEntries);
    }}
