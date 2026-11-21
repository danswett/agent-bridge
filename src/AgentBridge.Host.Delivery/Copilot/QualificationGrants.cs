using System.Globalization;

namespace AgentBridge.Host.Delivery.Copilot;

internal enum QualificationAccess { Denied, SreCli, OwnerApproval }
internal enum QualificationHostKind { Unknown, Disposable, Fleet }
internal enum QualificationGrantState { Issued, Consumed, Revoked, Expired }
internal sealed record QualificationBinding(string Head, Guid Vm, Guid Run, Guid Host,
    QueueTarget Target, string Challenge);
internal sealed record QualificationGrant(Guid Id, QualificationBinding Binding, DateTimeOffset Issued,
    DateTimeOffset Expires, QualificationGrantState State);
internal sealed record QualificationGrantDecision(QualificationGrant? Grant, string? Refusal)
{
    internal static QualificationGrantDecision Refuse(string reason) => new(null, reason);
}

// Host-owned adapters only: neither role nor disposable identity comes from a start request.
internal interface IQualificationTrust
{
    QualificationAccess Access { get; }
    QualificationHostKind HostKind { get; }
    QualificationBinding Binding { get; }
}

internal interface IQualificationGrantJournal
{
    QualificationGrant? Find(Guid id);
    // Atomically advance+durably flush the journal's time high-water; replay it after restart.
    DateTimeOffset ObserveTime(DateTimeOffset now);
    // Latest entry holding this challenge nonce under any id or state, durable across restart.
    QualificationGrant? FindChallenge(string nonce);
    // Atomically require the latest entries for next.Id AND next's nonce both to equal expected
    // (null: neither ever journaled, in any state), AND observedAt >= the durable time high-water,
    // then append+durably flush with that time. No overwrite or deletion.
    bool TryAppend(QualificationGrant? expected, QualificationGrant next, DateTimeOffset observedAt);
}

// Inactive until trusted control and durable Fleet journal adapters are supplied.
internal sealed class QualificationGrants(IQualificationTrust? trust = null,
    IQualificationGrantJournal? journal = null, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;

    internal QualificationGrantDecision Issue(Guid approval, TimeSpan lifetime)
    {
        if (!Authorized(start: false) || journal is null)
        { return QualificationGrantDecision.Refuse("qualification approval authority unavailable"); }
        var binding = trust!.Binding;
        if (approval == Guid.Empty || !Valid(binding) || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(60))
        { return QualificationGrantDecision.Refuse("qualification approval binding or lifetime invalid"); }
        var now = time.GetUtcNow();
        if (now < journal.ObserveTime(now)) { return QualificationGrantDecision.Refuse("qualification clock earlier than journal high-water"); }
        // A nonce names one queued turn; reuse under another approval id could bless a second target.
        if (journal.FindChallenge(binding.Challenge) is not null)
        { return QualificationGrantDecision.Refuse("qualification challenge nonce already used"); }
        var next = new QualificationGrant(approval, binding, now, now + lifetime, QualificationGrantState.Issued);
        return journal.TryAppend(null, next, now) ? new(next, null)
            : QualificationGrantDecision.Refuse("qualification approval already used");
    }

    internal QualificationGrantDecision Consume(Guid id, QueueTarget? target, QueueQualification.Challenge? challenge)
    {
        if (!Authorized(start: true) || journal is null)
        { return QualificationGrantDecision.Refuse("qualification.start requires SRE CLI on the approved disposable run"); }
        var old = journal.Find(id);
        if (old is null || !Matches(old, out var now) || id == Guid.Empty || old.Id != id
            || old.State != QualificationGrantState.Issued || !MatchesAttempt(old, target, challenge))
        { return QualificationGrantDecision.Refuse("qualification grant missing, expired, used or mismatched"); }
        var next = old with { State = QualificationGrantState.Consumed };
        if (!journal.TryAppend(old, next, now))
        { return QualificationGrantDecision.Refuse("qualification grant concurrently changed"); }
        return IsCurrent(next, target, challenge) ? new(next, null)
            : QualificationGrantDecision.Refuse("qualification grant invalidated during consumption");
    }

    internal bool IsCurrent(QualificationGrant grant, QueueTarget? target, QueueQualification.Challenge? challenge)
    {
        if (!Authorized(start: true) || journal is null) { return false; }
        return journal.Find(grant.Id) is { } old && Matches(old, out _) && grant.State == QualificationGrantState.Consumed
            && old == grant && MatchesAttempt(grant, target, challenge);
    }

    internal QualificationGrantDecision Revoke(Guid id)
    {
        if (trust?.Access != QualificationAccess.SreCli || trust.HostKind != QualificationHostKind.Disposable || journal is null)
        { return QualificationGrantDecision.Refuse("qualification revocation requires SRE CLI"); }
        for (var tries = 0; tries < 8; tries++)
        {
            var now = time.GetUtcNow();
            if (now < journal.ObserveTime(now)) { return QualificationGrantDecision.Refuse("qualification clock earlier than journal high-water"); }
            var old = journal.Find(id);
            if (id == Guid.Empty || old is null || old.Id != id || old.Binding != trust.Binding || old.State == QualificationGrantState.Revoked)
            { return QualificationGrantDecision.Refuse("qualification grant missing, foreign or already revoked"); }
            var next = old with { State = QualificationGrantState.Revoked };
            if (journal.TryAppend(old, next, now)) { return new(next, null); }
        }
        return QualificationGrantDecision.Refuse("qualification grant concurrently changed");
    }

    bool Authorized(bool start) => trust?.HostKind == QualificationHostKind.Disposable
        && (trust.Access == QualificationAccess.SreCli || !start && trust.Access == QualificationAccess.OwnerApproval);

    // Check time/expiry before every state or binding refusal, including consumed replays.
    bool Matches(QualificationGrant grant, out DateTimeOffset now)
    {
        now = time.GetUtcNow(); var high = journal!.ObserveTime(now);
        if (high >= grant.Expires) { Expire(grant.Id, high); return false; }
        if (now < high) { return false; }
        if (journal!.FindChallenge(grant.Binding.Challenge) != grant) { return false; }
        return Valid(grant.Binding) && grant.Binding == trust?.Binding && grant.Issued <= now
            && grant.Expires - grant.Issued <= TimeSpan.FromMinutes(60);
    }

    // An observed expiry must become durable even when another controller wins a CAS first.
    void Expire(Guid id, DateTimeOffset observedAt)
    {
        for (var tries = 0; journal!.Find(id) is { State: QualificationGrantState.Issued or QualificationGrantState.Consumed } latest; tries++)
        {
            if (tries == 8) { throw new IOException("qualification expiry could not be journaled"); }
            journal.TryAppend(latest, latest with { State = QualificationGrantState.Expired }, journal.ObserveTime(observedAt));
        }
    }

    static bool MatchesAttempt(QualificationGrant grant, QueueTarget? target, QueueQualification.Challenge? challenge) =>
        target is not null && challenge is not null && grant.Binding.Target == target && grant.Binding.Challenge == challenge.Text;

    static bool Valid(QualificationBinding binding)
    {
        const string prefix = "agent-bridge-qualify:";
        return Hex(binding.Head, 40) && binding.Vm != Guid.Empty && binding.Run != Guid.Empty && binding.Host != Guid.Empty
            && Complete(binding.Target) && binding.Challenge is { } nonce && nonce.StartsWith(prefix, StringComparison.Ordinal)
            && Hex(nonce[prefix.Length..], 64);
    }

    // Every QueueTarget field must be the decoded live identity shape; a default target binds nothing.
    static bool Complete(QueueTarget? t) => t is { ProcessId: > 0, StartedAt: > 0, InputMode: not 0 }
        && t.ConsoleId?.Split(':') is [var window, var owner, var started]
        && ulong.TryParse(window, NumberStyles.None, CultureInfo.InvariantCulture, out var hwnd) && hwnd != 0
        && uint.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid != 0
        && long.TryParse(started, NumberStyles.None, CultureInfo.InvariantCulture, out var created) && created > 0
        && new[] { t.CliPath, t.CliVersion, t.InjectorPath, t.InjectorVersion }.All(x => !string.IsNullOrWhiteSpace(x))
        && Hex(t.CliHash, 64) && Hex(t.InjectorHash, 64) && t.Protocol == KeyPlan.Header;

    static bool Hex(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit);
}
