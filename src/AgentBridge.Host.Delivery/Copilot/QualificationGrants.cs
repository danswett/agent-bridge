namespace AgentBridge.Host.Delivery.Copilot;

internal enum QualificationAccess { Denied, SreCli, OwnerApproval }
internal enum QualificationHostKind { Unknown, Disposable, Fleet }
internal enum QualificationGrantState { Issued, Consumed, Revoked }
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
    // Atomically compare the complete previous entry and append+durably flush the next.
    // Null means never issued, including revoked/expired history. No overwrite or deletion.
    bool TryAppend(QualificationGrant? expected, QualificationGrant next);
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
        && Matches(grant) && MatchesAttempt(grant, target, challenge) && journal.Find(grant.Id) == grant;

    internal QualificationGrantDecision Revoke(Guid id)
    {
        if (trust?.Access != QualificationAccess.SreCli || journal is null)
        { return QualificationGrantDecision.Refuse("qualification revocation requires SRE CLI"); }
        var old = journal.Find(id);
        if (id == Guid.Empty || old is null || old.Id != id)
        { return QualificationGrantDecision.Refuse("qualification grant missing"); }
        if (old.State == QualificationGrantState.Revoked) { return new(old, null); }
        var next = old with { State = QualificationGrantState.Revoked };
        return journal.TryAppend(old, next) ? new(next, null)
            : QualificationGrantDecision.Refuse("qualification grant concurrently changed");
    }

    bool Authorized(bool start) => trust?.HostKind == QualificationHostKind.Disposable
        && (trust.Access == QualificationAccess.SreCli || !start && trust.Access == QualificationAccess.OwnerApproval);

    bool Matches(QualificationGrant grant)
    {
        var now = time.GetUtcNow();
        return Valid(grant.Binding) && grant.Binding == trust?.Binding && grant.Issued <= now && now < grant.Expires
            && grant.Expires - grant.Issued <= TimeSpan.FromMinutes(60);
    }

    static bool MatchesAttempt(QualificationGrant grant, QueueTarget? target, QueueQualification.Challenge? challenge) =>
        target is not null && challenge is not null && grant.Binding.Target == target && grant.Binding.Challenge == challenge.Text;

    static bool Valid(QualificationBinding binding)
    {
        const string prefix = "agent-bridge-qualify:";
        return binding.Head is { Length: 40 } && binding.Head.All(char.IsAsciiHexDigit)
            && binding.Vm != Guid.Empty && binding.Run != Guid.Empty && binding.Host != Guid.Empty
            && binding.Target is not null && binding.Challenge is { } nonce
            && nonce.Length == prefix.Length + 64 && nonce.StartsWith(prefix, StringComparison.Ordinal)
            && nonce.Skip(prefix.Length).All(char.IsAsciiHexDigit);
    }
}
