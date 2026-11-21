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
    // Latest entry holding this challenge nonce under any id or state, durable across restart.
    QualificationGrant? FindChallenge(string nonce);
    // Atomically require the latest entries for next.Id AND next's nonce both to equal expected
    // (null: neither ever journaled, in any state), then append+durably flush. No overwrite or deletion.
    bool TryAppend(QualificationGrant? expected, QualificationGrant next);
}

// Inactive until trusted control and durable Fleet journal adapters are supplied.
internal sealed class QualificationGrants(IQualificationTrust? trust = null,
    IQualificationGrantJournal? journal = null, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object gate = new();
    DateTimeOffset latest = DateTimeOffset.MinValue;

    internal QualificationGrantDecision Issue(Guid approval, TimeSpan lifetime)
    {
        if (!Authorized(start: false) || journal is null)
        { return QualificationGrantDecision.Refuse("qualification approval authority unavailable"); }
        var binding = trust!.Binding;
        if (approval == Guid.Empty || !Valid(binding) || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(60))
        { return QualificationGrantDecision.Refuse("qualification approval binding or lifetime invalid"); }
        // A nonce names one queued turn; reuse under another approval id could bless a second target.
        if (journal.FindChallenge(binding.Challenge) is not null)
        { return QualificationGrantDecision.Refuse("qualification challenge nonce already used"); }
        var now = Now();
        var next = new QualificationGrant(approval, binding, now, now + lifetime, QualificationGrantState.Issued);
        return journal.TryAppend(null, next) ? new(next, null)
            : QualificationGrantDecision.Refuse("qualification approval already used");
    }

    internal QualificationGrantDecision Consume(Guid id, QueueTarget? target, QueueQualification.Challenge? challenge)
    {
        if (!Authorized(start: true) || journal is null)
        { return QualificationGrantDecision.Refuse("qualification.start requires SRE CLI on the approved disposable run"); }
        var old = journal.Find(id);
        if (id == Guid.Empty || old is null || old.Id != id || old.State != QualificationGrantState.Issued
            || !Matches(old) || !MatchesAttempt(old, target, challenge))
        { return QualificationGrantDecision.Refuse("qualification grant missing, expired, used or mismatched"); }
        var next = old with { State = QualificationGrantState.Consumed };
        if (!journal.TryAppend(old, next))
        { return QualificationGrantDecision.Refuse("qualification grant concurrently changed"); }
        return IsCurrent(next, target, challenge) ? new(next, null)
            : QualificationGrantDecision.Refuse("qualification grant invalidated during consumption");
    }

    internal bool IsCurrent(QualificationGrant grant, QueueTarget? target, QueueQualification.Challenge? challenge) =>
        Authorized(start: true) && journal is not null && grant.State == QualificationGrantState.Consumed
        && journal.Find(grant.Id) == grant && Matches(grant) && MatchesAttempt(grant, target, challenge);

    internal QualificationGrantDecision Revoke(Guid id)
    {
        if (trust?.Access != QualificationAccess.SreCli || trust.HostKind != QualificationHostKind.Disposable || journal is null)
        { return QualificationGrantDecision.Refuse("qualification revocation requires SRE CLI"); }
        var old = journal.Find(id);
        if (id == Guid.Empty || old is null || old.Id != id || old.Binding != trust.Binding)
        { return QualificationGrantDecision.Refuse("qualification grant missing or foreign"); }
        if (old.State == QualificationGrantState.Revoked) { return new(old, null); }
        var next = old with { State = QualificationGrantState.Revoked };
        return journal.TryAppend(old, next) ? new(next, null)
            : QualificationGrantDecision.Refuse("qualification grant concurrently changed");
    }

    bool Authorized(bool start) => trust?.HostKind == QualificationHostKind.Disposable
        && (trust.Access == QualificationAccess.SreCli || !start && trust.Access == QualificationAccess.OwnerApproval);

    // Wall time never runs backwards within one controller; restarts rely on journaled expiry.
    DateTimeOffset Now() { lock (gate) { var now = time.GetUtcNow(); return latest = now > latest ? now : latest; } }

    // One time sample decides expiry, and an observed expiry is journaled before refusing,
    // so a later wall-clock rollback cannot revive the grant.
    bool Matches(QualificationGrant grant)
    {
        var now = Now();
        if (now >= grant.Expires) { Expire(grant.Id); return false; }
        if (journal!.FindChallenge(grant.Binding.Challenge) != grant) { return false; }
        return Valid(grant.Binding) && grant.Binding == trust?.Binding && grant.Issued <= now
            && grant.Expires - grant.Issued <= TimeSpan.FromMinutes(60);
    }

    // An observed expiry must become durable even when another controller wins a CAS first.
    void Expire(Guid id)
    {
        for (var tries = 0; journal!.Find(id) is { State: QualificationGrantState.Issued or QualificationGrantState.Consumed } latest; tries++)
        {
            if (tries == 8) { throw new IOException("qualification expiry could not be journaled"); }
            journal.TryAppend(latest, latest with { State = QualificationGrantState.Expired });
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
