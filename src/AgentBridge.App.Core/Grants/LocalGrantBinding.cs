namespace AgentBridge.App.Core.Grants;

public sealed class LocalGrantBinding : IDisposable
{
    readonly HostConnection connection;
    readonly string profile;
    readonly Func<IGrantConfirmationSigner?> signer;
    readonly Action<IGrantIssueReceiver> bind;
    readonly Action unavailable;
    GrantWireReceiver? receiver;
    bool wasOnline, disposed;

    public LocalGrantBinding(HostConnection connection, string profile, Func<IGrantConfirmationSigner?> signer,
        Action<IGrantIssueReceiver> bind, Action unavailable)
    {
        this.connection = connection; this.profile = profile; this.signer = signer;
        this.bind = bind; this.unavailable = unavailable;
        connection.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (disposed) { return; }
        if (connection.State == LinkState.Connected && connection.Hello?.Machine.Id is { } id
            && receiver?.Receiver != id)
        {
            receiver = new GrantWireReceiver(new LocalGrantWireDispatcher(connection, id, profile), id, profile, signer);
            bind(receiver);
        }
        var online = receiver?.Online == true;
        if (wasOnline && !online) { unavailable(); }
        wasOnline = online;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        connection.Changed -= Refresh;
    }
}
