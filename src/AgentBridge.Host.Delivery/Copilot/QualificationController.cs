using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

internal enum QualificationSubmission { Unknown, Submitted, Unsupported }
internal interface IQualificationProbe
{
    Task<string?> CaptureAsync();
    IReadOnlyList<string>? Read();
    // Future injector adapter must honor cancellation and this fence under its final-effect gate.
    Task<QualificationSubmission> SendAsync(string challenge, Func<Task<bool>> fence, CancellationToken cancellationToken);
}
internal sealed record QualificationEvidence(string Head, string SnapshotHash, string NonceHash, string HostHash,
    string GrantHash, string VmHash, string RunHash, QueueCapability Capability, DateTimeOffset Expires);
internal sealed record QualificationRecord(QualificationEvidence Evidence, string ContentHash);
internal interface IQualificationReceiptJournal
{
    // Append-only Fleet receipts boundary: return only after durable flush, otherwise throw.
    void Append(QualificationRecord record);
}
internal sealed record QualificationStart(QueueCapability Capability, QualificationRecord? Record, string? Refusal);

// Source-only qualification.start core; no CLI, API, or live injector registers this.
internal sealed class QualificationController(QualificationGrants? authority = null, IQualificationProbe? probe = null,
    IQualificationReceiptJournal? receipts = null, TimeProvider? clock = null)
{
    readonly QualificationGrants grants = authority ?? new();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly SemaphoreSlim gate = new(1, 1);
    QualificationGrant? grant;
    QueueTarget? snapshot;
    QualificationRecord? active;
    long started;
    TimeSpan window;
    DateTimeOffset expires;

    internal async Task<QualificationStart> StartAsync(Guid grantId)
    {
        await gate.WaitAsync();
        try
        {
            if (grant is not null || probe is null || receipts is null)
            { return new(QueueCapability.Unknown, null, "qualification control unavailable or already started"); }
            var decision = grants.Consume(grantId);
            if (decision.Grant is null) { return new(QueueCapability.Unknown, null, decision.Refusal); }
            grant = decision.Grant;
            try
            {
                var now = time.GetUtcNow();
                window = grant.Expires - now;
                if (window > TimeSpan.FromMinutes(1)) { window = TimeSpan.FromMinutes(1); }
                if (window <= TimeSpan.Zero) { Revoke(); return new(QueueCapability.Unknown, null, "qualification grant expired"); }
                started = time.GetTimestamp(); expires = now + window;
                using var deadline = new CancellationTokenSource(window, time);
                var capability = QueueCapability.Unknown;
                QueueQualification.Challenge? challenge = null;
                try
                {
                    var source = await QueueQualification.CaptureAtLaunchAsync(probe.CaptureAsync, probe.Read, time).WaitAsync(deadline.Token);
                    snapshot = source.Snapshot;
                    challenge = await source.IssueAsync(window).WaitAsync(deadline.Token);
                    if (challenge is not null && await BoundAsync())
                    {
                        var sent = await probe.SendAsync(challenge.Text, BoundAsync, deadline.Token).WaitAsync(deadline.Token);
                        if (await BoundAsync())
                        {
                            capability = sent == QualificationSubmission.Unsupported ? QueueCapability.Unsupported
                                : sent == QualificationSubmission.Submitted ? await source.ObserveAsync(challenge).WaitAsync(deadline.Token)
                                : QueueCapability.Unknown;
                        }
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { capability = QueueCapability.Unknown; }
                if (!await BoundAsync()) { capability = QueueCapability.Unknown; }
                QualificationRecord Record(QueueCapability value)
                {
                    var binding = grant.Binding;
                    var evidence = new QualificationEvidence(binding.Head, Hash(snapshot), Hash(challenge?.Text),
                        Hash(binding.Host), Hash(grant.Id), Hash(binding.Vm), Hash(binding.Run), value, expires);
                    return new(evidence, Hash(evidence));
                }
                var record = Record(capability);
                receipts.Append(record);
                if (capability != QueueCapability.Unknown && !await BoundAsync())
                {
                    capability = QueueCapability.Unknown; record = Record(capability); receipts.Append(record);
                }
                if (capability == QueueCapability.Verified) { active = record; }
                else { Revoke(); }
                return new(capability, record, capability == QueueCapability.Verified ? null : "qualification evidence unknown or unsupported");
            }
            catch { active = null; Revoke(); throw; }
        }
        finally { gate.Release(); }
    }

    internal async Task<QueueCapability> CurrentAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (active is null) { return QueueCapability.Unknown; }
            if (await BoundAsync()) { return active.Evidence.Capability; }
            active = null; Revoke(); return QueueCapability.Unknown;
        }
        catch { active = null; Revoke(); throw; }
        finally { gate.Release(); }
    }

    internal async Task<bool> MatchesAsync(int pid, DateTimeOffset? created) =>
        created is not null && snapshot?.ProcessId == pid && snapshot.StartedAt == created.Value.ToFileTime()
        && await CurrentAsync() == QueueCapability.Verified;

    internal async Task<QualificationGrantDecision> EndAsync()
    {
        await gate.WaitAsync();
        try { active = null; return grant is null ? QualificationGrantDecision.Refuse("qualification not started") : grants.Revoke(grant.Id); }
        finally { gate.Release(); }
    }

    async Task<bool> BoundAsync()
    {
        if (grant is null || snapshot is null || probe is null || !Fresh() || !grants.IsCurrent(grant)) { return false; }
        var remaining = window - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) { return false; }
        try
        {
            var raw = await probe.CaptureAsync().WaitAsync(remaining, time);
            return QueueTarget.Decode(raw) == snapshot && Fresh() && grants.IsCurrent(grant);
        }
        catch (TimeoutException) { return false; }
    }

    bool Fresh() => time.GetElapsedTime(started) < window && time.GetUtcNow() < expires;
    void Revoke()
    {
        if (grant is not null && grants.Revoke(grant.Id).Refusal is { } refusal) { throw new InvalidOperationException(refusal); }
    }
    static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
