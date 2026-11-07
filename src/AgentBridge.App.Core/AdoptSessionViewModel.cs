using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// "Let this session message other sessions…" on a card (design/agent-principals.md §3.5): a person
// at this machine adopts a session the host found but did not launch. Two steps, so one stray
// press grants nothing: the first opens the question, with what it grants; only Confirm asks the
// host. Nothing is issued here: the host gives the credential to the session, when it asks.
// This machine's cards only: the host never offers it to a device.
public sealed class AdoptSessionViewModel : Observable
{
    readonly HostConnection host;
    readonly IUiDispatcher ui;
    readonly string sessionId;
    bool asking;
    bool busy;
    string scope = AgentProtocol.ScopeRead;
    string statusText = "";
    Tone statusTone;

    public AdoptSessionViewModel(string sessionId, HostConnection host, IUiDispatcher ui)
    {
        this.sessionId = sessionId;
        this.host = host;
        this.ui = ui;
    }

    public string Explanation => "This lets the session ask this machine for a credential, so it can act as an agent of its own and read your other sessions. It can't launch sessions, and it can't answer or reply for you unless you pick that. You can stop it at any time.";
    public static IReadOnlyList<string> Scopes => AgentProtocol.AdoptableScopes;

    public bool Asking { get => asking; private set { if (Set(ref asking, value)) { Raise(nameof(CanBegin)); } } }
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(CanBegin)); Raise(nameof(CanConfirm)); Raise(nameof(CanStop)); } } }
    public bool CanBegin => !Asking && !Busy;
    public bool CanConfirm => Asking && !Busy;
    public bool CanStop => !Busy;
    public string Scope { get => scope; set { if (AgentProtocol.AdoptableScopes.Contains(value)) { Set(ref scope, value); } } }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }

    public void Begin() { if (CanBegin) { StatusText = ""; Asking = true; } }
    public void Cancel() { if (!Busy) { Asking = false; } }

    public async Task ConfirmAsync()
    {
        if (!CanConfirm) { return; }
        Busy = true;
        try
        {
            var result = await host.CallAsync<AgentAdoptResult>(AgentProtocol.Adopt, new AgentAdoptRequest { Session = sessionId, Scope = scope }, TimeSpan.FromSeconds(10));
            Finish(false, $"Adopted as {result.Scope}. Now, inside that session, run `agent-bridge-host agents credential` within {AgentProtocol.AdoptionMinutes} minutes.", Tone.Ok);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Finish(true, $"Not adopted: {Words.Sentence(e.Message)}", Tone.Bad); }
    }

    public async Task StopAsync()
    {
        if (!CanStop) { return; }
        Busy = true;
        try
        {
            var result = await host.CallAsync<AgentRevokeResult>(AgentProtocol.Revoke, new AgentRevokeRequest { Session = sessionId }, TimeSpan.FromSeconds(10));
            Finish(false, result.Revoked ? "This session no longer acts as an agent." : "This session wasn't acting as an agent.", Tone.Ok);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Finish(true, $"Couldn't stop it: {Words.Sentence(e.Message)}", Tone.Bad); }
    }

    void Finish(bool keepAsking, string text, Tone tone) => ui.Post(() =>
    {
        Asking = keepAsking && Asking;
        StatusText = text;
        StatusTone = tone;
        Busy = false;
    });
}
