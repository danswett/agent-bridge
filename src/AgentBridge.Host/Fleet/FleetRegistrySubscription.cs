namespace AgentBridge.Host.Fleet;

// One authenticated connection's inert boundary; no relay or production wiring.
internal sealed class FleetRegistrySubscription
{
    readonly string account;
    readonly FleetRegistryCache cache;
    volatile bool optedIn;

    public FleetRegistrySubscription(string account, FleetRegistryCache cache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(cache);
        this.account = account;
        this.cache = cache;
    }

    // Called at the send boundary, never from an incoming frame.
    public void OptIn(string operation)
    {
        if (operation is not ("registry-get" or "registry-subscribe"))
        { throw new ArgumentException("Only registry-get or registry-subscribe opts in.", nameof(operation)); }
        cache.BeginRead();
        optedIn = true;
    }

    bool Admits(string frameAccount) => optedIn && string.Equals(frameAccount, account, StringComparison.Ordinal);

    public bool Apply(VerifiedRecord record, string frameAccount) =>
        Admits(frameAccount) && record.Account == account && cache.Accept(record);

    public bool Remove(string machine, string frameAccount)
    {
        if (!Admits(frameAccount)) { return false; }
        cache.Remove(machine);
        return true;
    }

    public bool Reconcile(IReadOnlySet<string> listed, string frameAccount)
    {
        if (!Admits(frameAccount)) { return false; }
        cache.Reconcile(listed);
        return true;
    }

    // A registry/known listing, not a registry-changed record, admits a removed ID again.
    public bool Relist(IReadOnlySet<string> listed, string frameAccount)
    {
        if (!Admits(frameAccount)) { return false; }
        cache.Relist(listed);
        return true;
    }
}
