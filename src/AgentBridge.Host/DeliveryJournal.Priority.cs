using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Host;

public sealed record PriorityCapability(Guid Id, string Principal, GrantIssuer Issuer, GrantAudience Audience,
    DateTimeOffset NotBefore, DateTimeOffset ExpiresAt, int Hourly = 3);
public sealed record DeliveryPriorityRequest(
    [property: JsonPropertyName("priority")] string Priority = "normal",
    [property: JsonPropertyName("reason")] string? Reason = null);
internal sealed record PriorityDecision(bool Accepted, string Priority, string? Reason = null);

public sealed partial class DeliveryJournal
{
    readonly List<DeliveryJournalRecord> priorityRecords = [];
    readonly HashSet<(string Principal, GrantAudience Receiver)> refusedPriority = [];
    string priorityTail = "";

    // Only trusted ingress may supply the authenticated person and captured receiver binding.
    internal void IssuePriority(PriorityCapability capability, DeliverySender person, GrantAudience receiver,
        Func<bool> current, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!person.Owner || !current() || !ValidPriority(capability) || capability.Issuer.Account != person.Id ||
                refusedPriority.Contains((capability.Principal, capability.Audience)) ||
                capability.Audience != receiver || capability.Issuer.IssuedAt > now ||
                now >= capability.ExpiresAt ||
                priorityRecords.Any(r => r.Capability?.Id == capability.Id))
            { throw new InvalidOperationException("Priority issue requires current person authority and a fresh bounded capability."); }
            PriorityRecord(capability.Id, "", person, "issued", now, capability);
        }
    }

    internal void RevokePriority(Guid capability, DeliverySender person, GrantAudience receiver,
        Func<bool> current, DateTimeOffset now)
    {
        lock (gate)
        {
            var cap = priorityRecords.FirstOrDefault(r => r.Reason == "issued" && r.Capability?.Id == capability)?.Capability;
            if (!person.Owner || !current() || cap is null || cap.Issuer.Account != person.Id || cap.Audience != receiver)
            { throw new InvalidOperationException("Priority revoke requires the authenticated issuing person and exact receiver."); }
            PriorityRecord(capability, "", person, "revoked", now, cap);
        }
    }

    internal PriorityDecision DecidePriority(Guid request, string target, DeliverySender sender, string body,
        DeliveryPriorityRequest requested, GrantAudience? receiver, DateTimeOffset now, Func<string, bool> admit)
    {
        lock (gate)
        {
            var previous = priorityRecords.LastOrDefault(r => r.Header.Id == request.ToString("D"));
            if (previous is not null && (previous.Reason is not ("lane-full" or "missing-reason" or "invalid-priority") ||
                previous.Session != target || previous.Header.From.Split(" (")[0] != new DeliverySender(sender.Id, Owner: sender.Owner).From))
            { return new(false, "normal", "priority request already considered; no renewed authority"); }
            PriorityRecord(request, target, sender, "requested", now,
                reason: requested.Reason is { Length: <= 256 } ? requested.Reason : null);
            if (requested.Priority is not ("normal" or "fyi" or "urgent"))
            { PriorityRecord(request, target, sender, "invalid-priority", now); return new(false, "normal", "invalid-priority"); }
            if (requested.Priority != "urgent") { return Admit(requested.Priority); }
            if (string.IsNullOrWhiteSpace(requested.Reason) || requested.Reason.Length > 256)
            { PriorityRecord(request, target, sender, "missing-reason", now); return new(false, "normal", "missing-reason"); }
            if (sender.Owner) { return Admit("urgent"); }
            var cap = priorityRecords.Where(r => r.Reason == "issued").Select(r => r.Capability!).ToArray()
                .FirstOrDefault(c => c.Principal == sender.Id && c.Audience == receiver && ActivePriority(c, now));
            var spent = priorityRecords.Count(r => r.Reason == "consumed" && r.Capability?.Principal == sender.Id &&
                r.Capability.Audience == receiver && now - r.RecordedAt < TimeSpan.FromHours(1));
            var refusal = cap is null ? "no-capability" : spent >= Math.Min(3, cap.Hourly) ? "quota" : null;
            return Admit(refusal is null ? "urgent" : "normal", cap, refusal);

            PriorityDecision Admit(string effective, PriorityCapability? capability = null, string? refused = null)
            {
                if (!admit(effective))
                { PriorityRecord(request, target, sender, "lane-full", now); return new(false, "normal", "lane-full"); }
                if (refused is not null)
                {
                    PriorityRecord(request, target, sender, refused, now);
                    PriorityRecord(request, target, sender, "demoted", now, reason: refused);
                }
                else if (effective == "urgent")
                {
                    PriorityRecord(request, target, sender, "granted", now, capability);
                    if (capability is not null) { PriorityRecord(request, target, sender, "consumed", now, capability); }
                }
                return new(true, effective, refused);
            }
        }
    }

    internal bool PriorityCurrent(Guid request, string target, DeliverySender sender, GrantAudience? receiver,
        DateTimeOffset now)
    {
        lock (gate)
        {
            var cap = priorityRecords.LastOrDefault(r => r.Reason == "consumed" && r.Header.Id == request.ToString("D") &&
                r.Session == target && r.Capability?.Principal == sender.Id)?.Capability;
            if (cap is null || sender.Owner || cap.Audience != receiver || !ActivePriority(cap, now)) { return false; }
            return !Uncertain && now >= priorityRecords[^1].RecordedAt;
        }
    }

    bool ActivePriority(PriorityCapability cap, DateTimeOffset now)
    {
        if (refusedPriority.Contains((cap.Principal, cap.Audience))) { return false; }
        if (priorityRecords.Any(r => r.Capability?.Id == cap.Id && r.Reason is "revoked" or "expired")) { return false; }
        if (now >= cap.ExpiresAt)
        {
            PriorityRecord(cap.Id, "", new(cap.Principal), "expired", now, cap);
            return false;
        }
        return now >= cap.NotBefore;
    }

    internal string? PriorityOutcome(Guid request)
    {
        lock (gate)
        {
            var record = priorityRecords.LastOrDefault(r => r.Header.Id == request.ToString("D"));
            return record?.Reason == "demoted" ? record.PriorityReason :
                record?.Reason is "no-capability" or "quota" or "missing-reason" or "invalid-priority" or "lane-full" ? record.Reason : null;
        }
    }

    static bool ValidPriority(PriorityCapability c) => c.Id != Guid.Empty && c.Principal is { Length: > 0 and <= 256 } &&
        c.Principal != "unknown" && c.Issuer is { Account.Length: > 0, Device.Length: 32, PersonProof.Length: > 0, Digest.Length: 64 } &&
        c.Audience is { Device.Length: 32, Profile.Length: > 0 } && c.Hourly is > 0 and <= 3 &&
        c.Issuer.IssuedAt != default && c.NotBefore >= c.Issuer.IssuedAt &&
        c.ExpiresAt > c.NotBefore && c.ExpiresAt - c.Issuer.IssuedAt <= TimeSpan.FromHours(24);

    void ReplayPriority(DeliveryJournalRecord record)
    {
        if (!Matches(record, "") || record.RecordedAt == default ||
            record.PriorityPrevious != priorityTail || !PriorityAction(record.Reason) ||
            record.Capability is { } cap && !ValidPriority(cap) ||
            priorityRecords.LastOrDefault() is { } previous && record.RecordedAt < previous.RecordedAt)
        {
            if (RefusePriorityChain(record)) { return; }
            throw new InvalidDataException("Priority journal evidence is invalid; no capability is usable.");
        }
        priorityRecords.Add(record);
        priorityTail = record.Hmac;
    }

    static bool PriorityAction(string? action) => action is "issued" or "revoked" or "expired" or "requested" or
        "granted" or "consumed" or "no-capability" or "quota" or "demoted" or "missing-reason" or "invalid-priority" or "lane-full";

    bool RefusePriorityChain(DeliveryJournalRecord record, bool legacy = false)
    {
        var cap = priorityRecords.LastOrDefault(r => r.Capability is { } c &&
            (r.Header.Id == record.Header.Id || c.Id.ToString("D") == record.Header.Id || c.Id == record.Capability?.Id))?.Capability;
        if (cap is null) { return false; }
        if (legacy && cap.Id.ToString("D") != record.Header.Id && record.Capability is null &&
            record.PriorityReason is null && record.PriorityPrevious is null && !PriorityAction(record.Reason) &&
            (record.Header.N != 0 || record.Header.Seq is not null || Matches(record, "")))
        { return false; }
        refusedPriority.Add((cap.Principal, cap.Audience));
        // Keep the hash cursor for later authenticated links, never the refused authority fact.
        priorityTail = record.Hmac;
        log("priority journal chain refused; no authority or quota renewal");
        return true;
    }

    void PriorityRecord(Guid request, string target, DeliverySender sender, string action, DateTimeOffset now,
        PriorityCapability? capability = null, string? reason = null)
    {
        if (Uncertain || priorityRecords.LastOrDefault() is { } previous && now < previous.RecordedAt)
        { throw new InvalidOperationException("Priority journal uncertain or clock moved backwards; no authority granted."); }
        var header = DeliveryEnvelope.Prepare(new(sender.From, request.ToString("D"), null, now, "normal", 0), "").Header;
        var record = new DeliveryJournalRecord(target, header, "priority", now, "", Reason: action)
            { Capability = capability, PriorityReason = reason, PriorityPrevious = priorityTail };
        record = record with { Hmac = Convert.ToBase64String(Mac(record, "")) };
        try { append(JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { Uncertain = true; log($"priority journal write failed ({e.GetType().Name}); no authority granted"); throw; }
        priorityRecords.Add(record);
        priorityTail = record.Hmac;
    }
}
