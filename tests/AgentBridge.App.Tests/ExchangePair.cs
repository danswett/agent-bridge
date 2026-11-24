using AgentBridge.App.Core;
using AgentBridge.Roster;
using Xunit;

// One real exchange (the roster library, no fakes for the crypto) between an approver and a joining device that share a handshake hash.
internal sealed class ExchangePair
{
    public sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public static readonly byte[] Genesis = Convert.FromHexString("7d0f3a2e1c4b4d5e00112233445566778899aabbccddeeff00112233445566ff");
    public readonly Clock Time = new();
    public readonly EnrollmentLockout LockA, LockJ;
    public readonly EnrollmentApprover Approver;
    public readonly JoinViewModel Join;

    public ExchangePair(EnrollmentLockout? lockA = null, EnrollmentLockout? lockJ = null, string pendingId = "3f9a2c71b0de4a5f0123456789abcdef")
    {
        var h = new byte[32];
        h[0] = 7;
        LockA = lockA ?? new(EnrollmentLockout.Counts.WrongEntries, () => Time.Now);
        LockJ = lockJ ?? new(EnrollmentLockout.Counts.EndedAttempts, () => Time.Now);
        Approver = new EnrollmentApprover("acct", h, pendingId, LockA, () => Time.Now);
        Join = new JoinViewModel(new EnrollmentJoiner("acct", h, LockJ, () => Time.Now));
    }

    // Commit, offer, reveal: both sides now hold the same transcript, and the joiner shows code 1.
    public void Exchange()
    {
        var nA = Approver.OnCommit(Join.Commit()!)!;
        Assert.True(Approver.OnReveal(Join.OnOffer(nA)!));
    }

    public string Code1 => Join.Code1.Replace(" ", "");
    public string WrongCode => Code1 == "000000" ? "111111" : "000000";
}
