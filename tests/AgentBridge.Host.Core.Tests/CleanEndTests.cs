using AgentBridge.Protocol;
using Xunit;

// Session watch S4e (docs/design/session-watch.md, R3 end sources): a CLI's own SessionEnd hook (Codex today) is the clean end the host can see; the
// source remembers it when it removes the session, so the watch can tell it from a vanish.
public class CleanEndTests
{
    const string A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    static void Running(Fixture f)
    {
        f.CodexRegistration(A, 800, f.CodexTranscript(A));
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        f.Processes.Holders[f.CodexLock(A)] = [800];
    }

    [Fact]
    public async Task A_Codex_session_whose_own_session_end_arrived_is_remembered_as_a_clean_end_when_it_is_removed()
    {
        using var f = new Fixture();
        Running(f);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());
        Assert.False(source.EndedByHook(A));

        f.CodexRegistration(A, 800, f.CodexTranscript(A), status: "ended", ended: true, updated: Fixture.T0.AddMinutes(5));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.True(source.EndedByHook(A));
    }

    [Fact]
    public async Task A_Codex_session_whose_process_just_vanished_is_not_a_clean_end()
    {
        using var f = new Fixture();
        Running(f);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        f.Processes.Exit(AgentKind.Codex, 800);
        f.Processes.Holders.Remove(f.CodexLock(A));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.False(source.EndedByHook(A));
    }

    [Fact]
    public async Task A_session_that_ended_cleanly_and_was_resumed_is_no_longer_remembered_as_ended()
    {
        using var f = new Fixture();
        Running(f);
        await using var source = f.Source();
        await source.RefreshAsync();
        f.CodexRegistration(A, 800, f.CodexTranscript(A), status: "ended", ended: true, updated: Fixture.T0.AddMinutes(5));
        await source.RefreshAsync();
        Assert.True(source.EndedByHook(A));

        // The same thread resumed in a new process, later than its end.
        f.CodexRegistration(A, 801, f.CodexTranscript(A), updated: Fixture.T0.AddMinutes(10));
        f.Processes.Run(AgentKind.Codex, 801, "codex.exe --no-daemon", Fixture.T0.AddMinutes(10));
        f.Processes.Holders[f.CodexLock(A)] = [801];
        await source.RefreshAsync();

        Assert.Single(source.Snapshot());
        Assert.False(source.EndedByHook(A));
    }
}
