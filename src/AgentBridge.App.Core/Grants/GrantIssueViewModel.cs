using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.App.Core.Grants;

public sealed class GrantIssueViewModel(IGrantIssueReceiver receiver) : Observable
{
    bool busy, submitted;
    public GrantIssuePreview? Preview { get; private set; }
    public GrantReceipt? Receipt { get; private set; }
    public string State { get; private set; } = "New grant";
    public string? Problem { get; private set; }
    public string Summary { get; private set; } = "";
    public string Document => Preview?.Document ?? "";
    public string Digest => Preview?.Digest ?? "";
    public GrantAllocation? Allocation => Preview?.Allocation;
    public string EffectivePolicy => Preview?.EffectivePolicy ?? "";
    public ImmutableArray<string> UnavailableActions => Preview?.UnavailableActions ?? [];
    public bool Active => Receipt is not null && Problem is null;
    public bool CanConfirm => Preview is not null && !busy && !submitted && receiver.Online
        && receiver.Problem is null && Problem is null or "person-required";

    public async Task PrepareAsync(JsonElement document, CancellationToken ct = default)
    {
        if (busy || submitted) { return; }
        Preview = null; Receipt = null; Problem = null; Summary = "";
        busy = true; State = "Preparing"; RaiseAll();
        try
        {
            if (!receiver.Online || receiver.Problem is not null)
            { Refuse(receiver.Problem ?? "target-offline"); return; }
            var result = await receiver.PrepareIssueAsync(document.Clone(), ct);
            if (result.Problem is not null || result.Preview is not { } preview)
            { Refuse(result.Problem ?? "unavailable"); return; }
            if (preview.Receiver != receiver.Receiver || !MatchesDocument(preview))
            { Refuse("digest-mismatch"); return; }
            Preview = preview;
            using var summary = JsonDocument.Parse(preview.Document);
            Summary = string.Join("\n", summary.RootElement.EnumerateObject()
                .Select(field => $"{field.Name}: {Describe(field.Value)}"));
            if (preview.UnavailableActions.IsDefault || preview.UnavailableActions.Length > 0)
            { Refuse("action-unavailable"); return; }
            State = "Awaiting person confirmation";
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException
            or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { Refuse("unavailable"); }
        finally { busy = false; RaiseAll(); }
    }

    public async Task ConfirmAsync(string personDigest, CancellationToken ct = default)
    {
        if (busy || submitted || Preview is not { } preview) { return; }
        if (!receiver.Online || receiver.Problem is not null)
        { Refuse(receiver.Problem ?? "target-offline"); RaiseAll(); return; }
        if (personDigest != preview.Digest) { Refuse("person-required"); RaiseAll(); return; }
        if (!CanConfirm) { RaiseAll(); return; }
        busy = true; submitted = true; Problem = null; State = "Issuing"; RaiseAll();
        try
        {
            var result = await receiver.ConfirmIssueAsync(preview.Id, preview.Generation, preview.Digest, ct);
            if (result.Problem is not null) { Refuse(result.Problem); return; }
            if (result.Receipt is not { } receipt || receipt.Id != preview.Id || receipt.Generation != preview.Generation
                || receipt.Receiver != preview.Receiver || receipt.Digest != preview.Digest)
            { Refuse("receipt-mismatch"); return; }
            Receipt = receipt; State = "Active";
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
        { Problem = "unconfirmed"; State = "Unconfirmed"; }
        finally { busy = false; RaiseAll(); }
    }

    void Refuse(string problem)
    {
        Problem = problem;
        State = problem is "RotationRequired" or "rotation-required" ? "Rotation required" : "Not issued";
    }

    static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => string.Join("; ", value.EnumerateObject().Select(p => $"{p.Name}: {Describe(p.Value)}")),
        JsonValueKind.Array => value.GetArrayLength() == 0 ? "none" : string.Join(", ", value.EnumerateArray().Select(Describe)),
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText()
    };

    static bool MatchesDocument(GrantIssuePreview preview)
    {
        if (preview.Document.Length > 64 * 1024 || preview.Id == Guid.Empty || preview.Generation <= 0
            || preview.Allocation is not { Hourly: > 0 and <= 1000, Daily: > 0 and <= 5000, Live: > 0 and <= 100 }
            || preview.Allocation.Hourly > preview.Allocation.Daily
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Document))) != preview.Digest)
        { return false; }
        using var json = JsonDocument.Parse(preview.Document);
        var d = json.RootElement;
        return d.GetProperty("id").GetGuid() == preview.Id && d.GetProperty("generation").GetInt64() == preview.Generation
            && d.GetProperty("audience").GetProperty("device").GetString() == preview.Receiver
            && d.GetProperty("audience").GetProperty("profile").GetString() == preview.Profile
            && d.GetProperty("caps").GetProperty("hourly").GetInt32() == preview.Allocation.Hourly
            && d.GetProperty("caps").GetProperty("daily").GetInt32() == preview.Allocation.Daily
            && d.GetProperty("caps").GetProperty("live").GetInt32() == preview.Allocation.Live;
    }
}
