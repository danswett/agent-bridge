namespace AgentBridge.Rehearsal;

// Turn progress and idle settling, independent of how a session is observed or answered.
internal sealed class TurnCompletion(bool compact, bool background, string? initialMarker, DateTimeOffset began)
{
    string? marker = initialMarker;
    DateTimeOffset? idleSince;

    public bool SawWork { get; private set; }
    public bool Moved { get; private set; }

    public void Observe(string status, string currentMarker)
    {
        if (status is "working" or "waiting" or "agents" or "shell") { SawWork = true; }
        // A launch starts from its first observation; a later turn supplies its pre-send marker.
        marker ??= currentMarker;
        if (currentMarker != marker) { Moved = true; }
    }

    public void Question() => idleSince = null;

    public bool Complete(string status, bool questionOpen, bool answered, bool replied, DateTimeOffset at)
    {
        // Compaction can finish before the first observation without changing the response.
        var quietCompact = compact && at - began >= TimeSpan.FromMinutes(1);
        if (status == "idle" && !questionOpen && (answered || SawWork || Moved || replied || quietCompact))
        {
            idleSince ??= at;
            // Background work may wake the session again after an apparent end.
            return at - idleSince >= TimeSpan.FromSeconds(background ? 45 : 15);
        }
        idleSince = null;
        return false;
    }
}
