using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// One of the account's machines that is offline, as the relay's roster remembers it (#50,
// docs/protocol/roster.md): its name, when it was last seen, and Forget, which takes two
// presses as End session does. The relay accepts a forget only from a person's device (this
// app is one), and only for a machine that is not connected. A machine that comes back leaves
// the list, and is a new entry, not armed, if it goes again.
public sealed class OfflineMachineViewModel : Observable
{
    public const int ConfirmSeconds = 5;

    readonly Func<string, CancellationToken, Task<string?>> forget;
    readonly TimeProvider time;
    string name;
    DateTimeOffset lastSeen;
    DateTimeOffset? armedAt;
    bool busy;
    string? error;

    public OfflineMachineViewModel(KnownMachine m, Func<string, CancellationToken, Task<string?>> forget, TimeProvider time)
    {
        Id = m.Id;
        name = m.Name;
        lastSeen = m.LastSeen;
        this.forget = forget;
        this.time = time;
    }

    public string Id { get; }
    public string Name => name;
    public DateTimeOffset LastSeen => lastSeen;
    public string LastSeenText => $"Offline \u00b7 last seen {Words.Ago(lastSeen, time.GetUtcNow())}";
    public bool Armed => armedAt is not null;
    public bool Busy => busy;
    public bool CanForget => !busy;
    public string ForgetText => busy ? "Forgetting\u2026" : Armed ? "Press again to forget" : "Forget";
    // What a screen reader says for the button.
    public string ForgetName => Armed ? $"Forget {name}: press again to confirm, it stops being listed" : $"Forget {name}";
    public string? Error => error;
    public bool HasError => error is not null;

    internal void Update(KnownMachine m)
    {
        if (name != m.Name) { name = m.Name; Raise(nameof(Name)); Raise(nameof(ForgetName)); }
        if (lastSeen != m.LastSeen) { lastSeen = m.LastSeen; Raise(nameof(LastSeen)); Raise(nameof(LastSeenText)); }
    }

    // For the button: PressAsync, whose outcome shows on the entry.
    public void Press() => _ = PressAsync();

    // The first press arms, for ConfirmSeconds; the second, while armed, asks the relay.
    public async Task PressAsync()
    {
        if (busy) { return; }
        // The tick that lapses an arm comes once a second, or later on a busy dispatcher: a
        // press after the deadline arms again, whether or not that tick has come.
        if (armedAt is { } at && time.GetUtcNow() - at >= TimeSpan.FromSeconds(ConfirmSeconds)) { armedAt = null; }
        if (!Armed)
        {
            armedAt = time.GetUtcNow();
            error = null;
            Changed();
            return;
        }
        armedAt = null;
        busy = true;
        Changed();
        string? why;
        try { why = await forget(Id, CancellationToken.None); }
        // Whatever the connection threw (a send on a socket that just dropped throws
        // WebSocketException): the press must end, not leave the button stuck on "Forgetting…".
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidOperationException
            or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            why = "unreachable";
        }
        busy = false;
        error = why is null ? null : Explain(why);
        Changed();
    }

    // Each second: an arm lapses, and "last seen" moves on.
    internal void Tick()
    {
        if (armedAt is { } at && time.GetUtcNow() - at >= TimeSpan.FromSeconds(ConfirmSeconds))
        {
            armedAt = null;
            Changed();
        }
        Raise(nameof(LastSeenText));
    }

    public static string Explain(string why) => why switch
    {
        "online" => "It is back online, so it was not forgotten.",
        "not-found" => "The relay no longer lists it.",
        "rate-limited" => "Too many machines forgotten just now. Try again in a minute.",
        "not-allowed" => "The relay refused: only a person's device can forget a machine.",
        "unsupported" or "disabled" => "The relay cannot forget machines yet.",
        "unreachable" => "The relay did not answer. Try again when connected.",
        _ => $"The relay refused: {why}.",
    };

    void Changed()
    {
        Raise(nameof(Armed));
        Raise(nameof(Busy));
        Raise(nameof(CanForget));
        Raise(nameof(ForgetText));
        Raise(nameof(ForgetName));
        Raise(nameof(Error));
        Raise(nameof(HasError));
    }
}
