using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Between this machine's updater and its host (#51, docs/protocol/v1-update.md): tells the host
// what the updater is doing (host.update.report), which it serves to the account's other devices,
// and hands the updater the presses of Check and Install the host carries from them
// (HostStatus.UpdateAsks), each once, telling the host it has taken them. A copy of the app the
// updater cannot update (a build folder) reports that, and takes nothing: the host then refuses
// the presses itself.
public sealed class UpdateReporter : IDisposable
{
    const int Remembered = 64;

    readonly HostConnection host;
    readonly AutoUpdater? updater;
    readonly Action<string> log;
    readonly Queue<string> order = new();
    readonly HashSet<string> taken = new(StringComparer.Ordinal);
    readonly SemaphoreSlim sending = new(1, 1);
    // What the connection it went to was last sent, so an unchanged state is not sent again and a
    // new connection is sent it at once: the host forgets the app when its connection closes, a
    // pipe dropped under a host that kept running included, so the host's run alone is not
    // enough to key it by.
    string? sentTo;
    string? sent;
    string? triedTo;
    bool failed;

    public UpdateReporter(HostConnection host, AutoUpdater? updater, Action<string> log)
    {
        this.host = host;
        this.updater = updater;
        this.log = log;
        host.Changed += OnHost;
        if (updater is not null) { updater.StateChanged += Report; }
        OnHost();
    }

    public UpdateState State => updater?.State ?? new UpdateState { Installer = false, Unavailable = AutoUpdater.NotInstalled };

    // The connection changes for every session event too: this sends only for a host instance not
    // yet tried, or asks to take or to say were taken.
    void OnHost()
    {
        if (host.State != LinkState.Connected || host.Status is not { } status) { return; }
        var listed = false;
        List<UpdateAsk> fresh = [];
        lock (taken)
        {
            foreach (var ask in status.UpdateAsks ?? [])
            {
                if (!UpdateProtocol.IsRequest(ask.Request)) { continue; }
                if (updater is null || taken.Contains(ask.Request)) { listed |= updater is not null; continue; }
                taken.Add(ask.Request);
                order.Enqueue(ask.Request);
                while (order.Count > Remembered) { taken.Remove(order.Dequeue()); }
                fresh.Add(ask);
            }
        }
        foreach (var ask in fresh) { _ = updater!.Ask(ask.Kind, ask.Request); }
        if (fresh.Count > 0 || listed || Connection() != Volatile.Read(ref triedTo)) { Report(); }
    }

    // Sends the state when it, or the host instance, has changed since the last send, or the host
    // still lists an ask this app has taken.
    public void Report() => _ = ReportAsync();

    public async Task ReportAsync()
    {
        await sending.WaitAsync();
        try
        {
            if (host.State != LinkState.Connected || host.Status is not { } status) { return; }
            var instance = Connection();
            Volatile.Write(ref triedTo, instance);
            List<string> done;
            lock (taken) { done = [.. (status.UpdateAsks ?? []).Select(a => a.Request).Where(taken.Contains)]; }
            var report = new UpdateReport(State, done.Count > 0 ? done : null);
            var json = JsonSerializer.Serialize(report, ProtocolJson.Options);
            if (instance == sentTo && json == sent) { return; }
            await host.CallAsync(HostOps.UpdateReport, report, TimeSpan.FromSeconds(10));
            (sentTo, sent, failed) = (instance, json, false);
        }
        // An older host without the op, or one going away: tried again at the next change. Said
        // once, not at every one.
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            if (!failed) { log($"update: the host was not told the updater's state: {e.Message}"); }
            failed = true;
        }
        finally { sending.Release(); }
    }

    string Connection() => $"{host.ConnectionNumber}:{host.HostInstance}";

    public void Dispose()
    {
        host.Changed -= OnHost;
        if (updater is not null) { updater.StateChanged -= Report; }
    }
}
