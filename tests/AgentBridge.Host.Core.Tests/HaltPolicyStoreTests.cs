using System.Text;
using AgentBridge.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AgentBridge.Host.Core.Updates.HaltPolicyOutcome;

namespace AgentBridge.Host.Core.Tests;

// Synthetic policy bodies (as verified upstream), an injected clock and a temporary folder; each
// Store() is a restart. Nothing here asks GitHub.
public sealed class HaltPolicyStoreTests : IDisposable
{
    static readonly DateTimeOffset Issued = new(2026, 11, 21, 23, 0, 0, TimeSpan.Zero);
    readonly string folder = Path.Combine(Path.GetTempPath(), "halt-policy-store-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(Issued.AddHours(1));

    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }

    HaltPolicyStore Store(TimeProvider? at = null) => new(folder, at ?? clock);

    static HaltPolicyEnvelope Policy(long sequence, string halted = "\"0.1.7214\"", long run = 7300, DateTimeOffset? issued = null)
    {
        var (from, tag) = ((issued ?? Issued).UtcDateTime, $"v0.1.{run}");
        return HaltPolicyReader.Parse(Encoding.UTF8.GetBytes($"{{\"v\":1,\"repository\":\"danswett/agent-bridge\",\"purpose\":\"update-halt-policy\",\"sequence\":{sequence},"
            + $"\"issuedAt\":\"{from:yyyy-MM-dd'T'HH:mm:ss'Z'}\",\"expiresAt\":\"{from.AddHours(6):yyyy-MM-dd'T'HH:mm:ss'Z'}\",\"halted\":[{halted}],\"releases\":[{{\"tag\":\"{tag}\","
            + $"\"channel\":\"win\",\"prerelease\":true,\"feed\":\"https://github.com/danswett/agent-bridge/releases/download/{tag}/releases.win.json\",\"sha256\":\"{new string('a', 64)}\",\"size\":1}}]}}"));
    }

    async Task<HaltPolicyOutcome> Outcome(HaltPolicyEnvelope? read, string? version, HaltPolicyStore? store = null) =>
        (await (store ?? Store()).CheckAsync(read, version)).Outcome;

    [Fact]
    public async Task A_verified_read_is_saved_and_a_restart_keeps_it_only_within_its_signed_validity()
    {
        Assert.Equal(Verified, await Outcome(Policy(42), "0.1.7300"));
        Assert.True(File.Exists(Path.Combine(folder, HaltPolicyStore.FileName)));
        var restarted = await Store().CheckAsync(null, "0.1.7300");
        Assert.Equal((Verified, 42L, null), (restarted.Outcome, restarted.Policy!.Sequence, restarted.Reason));
        clock.SetUtcNow(Issued.AddHours(6));
        Assert.Equal(Unavailable, await Outcome(null, "0.1.7300"));
        Assert.Equal(Unavailable, await Outcome(Policy(42), "0.1.7300"));
    }

    [Fact]
    public async Task Known_halts_always_refuse_even_once_expired_or_unsaved()
    {
        await Outcome(Policy(42), null);
        clock.SetUtcNow(Issued.AddDays(2));
        Assert.Equal(KnownHalted, await Outcome(null, "0.1.7214"));
        Assert.Equal(Unavailable, await Outcome(null, "0.1.7300"));
        Assert.Equal(KnownHalted, await Outcome(null, "0.1.7214", new HaltPolicyStore(folder, clock) { Replace = (_, _) => throw new IOException("disk full") }));
    }

    [Fact]
    public async Task A_failed_save_or_a_held_lock_is_unavailable_but_keeps_what_it_read_in_memory()
    {
        var unsaved = new HaltPolicyStore(folder, clock) { Replace = (_, _) => throw new IOException("disk full") };
        Assert.Equal(Unavailable, await Outcome(Policy(42), "0.1.7300", unsaved));
        Assert.Equal(KnownHalted, await Outcome(null, "0.1.7214", unsaved));
        Assert.False(File.Exists(Path.Combine(folder, HaltPolicyStore.FileName)));
        Directory.CreateDirectory(folder);
        using var held = new FileStream(Path.Combine(folder, "release-discovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(Unavailable, await Outcome(Policy(42), "0.1.7300", new HaltPolicyStore(folder, clock) { LockWait = TimeSpan.Zero }));
    }

    [Fact]
    public async Task A_rolled_back_or_future_policy_is_refused_and_only_a_higher_sequence_clears_a_halt()
    {
        await Outcome(Policy(42), null);
        Assert.Equal(Unavailable, await Outcome(Policy(41, halted: ""), "0.1.7300"));
        Assert.Equal(KnownHalted, await Outcome(Policy(41, halted: ""), "0.1.7214"));
        Assert.Equal(Unavailable, await Outcome(Policy(43, halted: "", issued: Issued.AddHours(2)), "0.1.7300"));
        Assert.Equal(KnownHalted, await Outcome(null, "0.1.7214"));
        var cleared = await Store().CheckAsync(Policy(43, halted: ""), "0.1.7214");
        Assert.Equal((Verified, 43L), (cleared.Outcome, cleared.Policy!.Sequence));
    }

    [Fact]
    public async Task Replaying_the_same_body_is_idempotent_but_another_body_at_that_sequence_stays_unavailable_until_a_higher_one()
    {
        await Outcome(Policy(42), null);
        Assert.Equal(Verified, await Outcome(Policy(42), "0.1.7300"));
        Assert.Equal(Unavailable, await Outcome(Policy(42, halted: ""), "0.1.7300"));
        Assert.Equal(KnownHalted, await Outcome(null, "0.1.7214"));
        Assert.Equal(Unavailable, await Outcome(Policy(42), "0.1.7300"));
        Assert.Equal(Verified, await Outcome(Policy(43), "0.1.7300"));
    }

    [Fact]
    public async Task A_clock_put_back_after_a_restart_is_unavailable_and_cannot_revive_an_expired_sequence()
    {
        await Outcome(Policy(42), null);
        clock.SetUtcNow(Issued.AddHours(6));
        await Outcome(null, null);
        var back = Store(new FakeTimeProvider(Issued.AddHours(2)));
        var verdict = await back.CheckAsync(Policy(42), "0.1.7300");
        Assert.Equal(Unavailable, verdict.Outcome);
        Assert.NotNull(verdict.Reason);
        Assert.Equal(KnownHalted, await Outcome(null, "0.1.7214", back));
    }

    [Fact]
    public async Task Channel_floors_rise_only_with_verified_policies()
    {
        await Outcome(Policy(42, run: 7300), null);
        Assert.Equal(7300, (await Store().CheckAsync(Policy(43, run: 7290), null)).Floors["win/prerelease"]);
        Assert.Equal(7310, (await Store().CheckAsync(Policy(44, run: 7310), null)).Floors["win/prerelease"]);
        Assert.Equal(7310, (await Store().CheckAsync(Policy(40, run: 7400), null)).Floors["win/prerelease"]);
    }

    [Fact]
    public async Task Unreadable_state_is_a_new_install_never_a_pass()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, HaltPolicyStore.FileName), "{\"body\":\"not base64\"");
        Assert.Equal(Unavailable, await Outcome(null, "0.1.7300"));
        Assert.Equal(Verified, await Outcome(Policy(41), "0.1.7300"));
    }
}
