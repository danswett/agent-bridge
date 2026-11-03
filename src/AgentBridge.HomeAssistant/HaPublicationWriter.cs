using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// Only the fenced publisher can mint a shared write; there is no generic public
// registrar through which the reserved policy/card can bypass authorization.
public sealed class HaPublicationMutation
{
    readonly JsonObject command;
    internal HaPublicationMutation(JsonObject command) => this.command = (JsonObject)command.DeepClone();
    internal JsonObject Command() => (JsonObject)command.DeepClone();
}

internal sealed record HaPublicationIdentity(string Authority, string Participant, string Writer, int Generation);

internal static class HaPublicationJson
{
    // Even UnsafeRelaxedJsonEscaping escapes surrogate pairs; ConvertTo-Json
    // preserves them. Protocol digests must match for emoji display names too.
    public static string Canonical(JsonNode? value)
    {
        var text = new StringBuilder();
        Write(value, text);
        return text.ToString();
    }
    static void Write(JsonNode? value, StringBuilder text)
    {
        switch (value)
        {
            case JsonObject obj:
                text.Append('{');
                var separator = "";
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    text.Append(separator); separator = ",";
                    Quote(pair.Key, text); text.Append(':'); Write(pair.Value, text);
                }
                text.Append('}');
                break;
            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++) { if (i > 0) { text.Append(','); } Write(array[i], text); }
                text.Append(']');
                break;
            case JsonValue item when item.TryGetValue<string>(out var word):
                Quote(word, text);
                break;
            default:
                text.Append(value?.ToJsonString() ?? "null");
                break;
        }
    }
    static void Quote(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (var c in value)
        {
            var escaped = c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => null };
            if (escaped is not null) { text.Append(escaped); }
            else if (c < 32 || c is '\u0085' or '\u2028' or '\u2029') { text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); }
            else { text.Append(c); }
        }
        text.Append('"');
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed record HaVerifiedPublication(bool Verified, string InputHash, IReadOnlySet<string> RenderedNodes, string Reason)
{
    public static HaVerifiedPublication Read(HaPublicationObservation state)
    {
        HaVerifiedPublication Refuse(string why) => new(false, "", new HashSet<string>(), why);
        var policy = state.Policy;
        if (policy?.Pinned == true)
        {
            try
            {
                if (HaPublicationArtifact.ReadInlineCard(state.CardUrl) != policy.Card) { return Refuse("pinned card content does not match"); }
            }
            catch (InvalidDataException) { return Refuse("pinned card content cannot be verified"); }
        }
        if (policy is null || !state.Dashboard.Found) { return Refuse("missing policy or published receipt"); }
        var config = JsonNode.Parse(state.Dashboard.Json!)!.AsObject();
        if (!config.TryGetPropertyValue("agent_bridge_publication", out var value)) { return Refuse("missing published receipt"); }
        if (value is not JsonObject receipt) { throw new InvalidDataException("Malformed dashboard publication receipt."); }
        using var document = JsonDocument.Parse(receipt.ToJsonString());
        var json = document.RootElement;
        foreach (var key in new[] { "protocol", "authority", "generation", "writer", "render", "inputHash", "contentHash", "cardUrlHash", "renderedNodes" })
        {
            if (!json.TryGetProperty(key, out _)) { throw new InvalidDataException("Incomplete dashboard publication receipt."); }
        }
        if (!Integer(json.GetProperty("protocol"), out var protocol) || protocol != 1 ||
            !Integer(json.GetProperty("generation"), out var generation) ||
            json.GetProperty("renderedNodes").ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Malformed dashboard publication receipt types.");
        }
        var authority = HaPublicationPolicy.Text(json, "authority");
        var writer = HaPublicationPolicy.Text(json, "writer");
        var input = Digest(json, "inputHash");
        var content = Digest(json, "contentHash");
        var card = Digest(json, "cardUrlHash");
        var render = HaPublicationArtifact.Read(json.GetProperty("render"));
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in json.GetProperty("renderedNodes").EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.String || node.GetString() is not { Length: > 0 } text ||
                text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            {
                throw new InvalidDataException("Malformed dashboard rendered-node set.");
            }
            nodes.Add(text);
        }
        receipt.Remove("contentHash");
        if (authority != policy.Authority || generation != policy.Generation || writer != policy.Writer) { return Refuse("published authority/generation does not match policy"); }
        if (render != policy.Render) { return Refuse("published renderer does not match policy"); }
        if (card != HaPublicationJson.Hash(state.CardUrl)) { return Refuse("served card changed"); }
        if (content != HaPublicationJson.Hash(HaPublicationJson.Canonical(config))) { return Refuse("published content/receipt digest mismatch"); }
        return new(true, input, nodes, "");
    }

    static bool Integer(JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
    static string Digest(JsonElement json, string name)
    {
        var text = HaPublicationPolicy.Text(json, name);
        return HaPublicationPolicy.IsHash(text) ? text : throw new InvalidDataException("Malformed dashboard publication receipt hash.");
    }
}

internal sealed partial class HaPublicationWriter(IHomeAssistant ha, string dashboard, HaPublicationReceipts receipts, HaPublicationIdentity identity)
{
    // Targets come only from locally configured endpoints. This process-local
    // transaction gate is shared across client/profile instances, not a writer lease.
    static readonly Lock transactionSync = new();
    static readonly Dictionary<string, TransactionEntry> transactions = new(StringComparer.Ordinal);
    sealed class TransactionEntry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int References;
    }
    sealed class Transaction(string key, TransactionEntry entry) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
            entry.Gate.Release();
            ReleaseEntry(key, entry);
        }
    }
    static void ReleaseEntry(string key, TransactionEntry entry)
    {
        lock (transactionSync)
        {
            if (--entry.References != 0) { return; }
            transactions.Remove(key);
            entry.Gate.Dispose();
        }
    }
    async Task<IDisposable> BeginAsync(CancellationToken ct)
    {
        var key = receipts.PublicationKey;
        TransactionEntry entry;
        lock (transactionSync)
        {
            if (!transactions.TryGetValue(key, out entry!)) { transactions.Add(key, entry = new()); }
            entry.References++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            return new Transaction(key, entry);
        }
        catch { ReleaseEntry(key, entry); throw; }
    }

    public Task<HaPublicationObservation> ReadAsync(CancellationToken cancellationToken = default) =>
        HaPublicationObservation.ReadAsync(ha, dashboard, receipts, cancellationToken);

    void Authorize(HaPublicationObservation state, HaPublicationComponent component, HaPublicationArtifact artifact)
    {
        if (state.Policy is null) { throw new InvalidDataException("Explicit publication bootstrap or migration is required; automatic writers never establish authority."); }
        state.Policy.CheckWriter(identity.Authority, identity.Participant, identity.Writer, identity.Generation, component, artifact, state.CardUrl);
    }

    async Task<HaPublicationObservation> FenceAsync(HaPublicationComponent component, HaPublicationArtifact artifact, CancellationToken ct,
        string? renderedCardUrl = null)
    {
        var state = await ReadAsync(ct);
        if (renderedCardUrl is not null && state.CardUrl != renderedCardUrl)
        {
            throw new InvalidDataException("The served card changed since rendering; render again before advancing the fence.");
        }
        Authorize(state, component, artifact);
        var policy = state.Policy!;
        var target = component == HaPublicationComponent.Card ? policy.Card : policy.Render;
        if (target == artifact) { return state; }
        var next = policy.ToJson();
        var key = component == HaPublicationComponent.Card ? "card" : "render";
        next[key] = artifact.ToJson();
        next[component == HaPublicationComponent.Card ? "highCard" : "highRender"] = artifact.ToJson();
        using var document = JsonDocument.Parse(next.ToJsonString());
        var advanced = HaPublicationPolicy.Read(document.RootElement, dashboard);
        advanced.CheckProgressFrom(policy);
        await ResourceAsync(state.PolicyResource!.Id, advanced.ResourceUrl, ct);
        state = await ReadAsync(ct);
        if (state.Policy?.Hash != advanced.Hash) { throw new InvalidDataException("Publication fence write was not confirmed; no artifact write is authorized."); }
        return state;
    }

    Task ResourceAsync(string? id, string url, CancellationToken ct)
    {
        var command = new JsonObject { ["type"] = id is null ? "lovelace/resources/create" : "lovelace/resources/update", ["url"] = url, ["res_type"] = "module" };
        if (id is not null) { command["resource_id"] = id; }
        return ha.ApplyPublicationAsync(new(command), ct);
    }

    public async Task PublishCardAsync(string inlineCardUrl, CancellationToken cancellationToken = default)
    {
        using var transaction = await BeginAsync(cancellationToken);
        var artifact = HaPublicationArtifact.ReadInlineCard(inlineCardUrl);
        var state = await FenceAsync(HaPublicationComponent.Card, artifact, cancellationToken);
        if (state.CardUrl == inlineCardUrl) { return; }
        await ResourceAsync(state.CardResource?.Id, inlineCardUrl, cancellationToken);
        var confirmed = await ReadAsync(cancellationToken);
        Authorize(confirmed, HaPublicationComponent.Card, artifact);
        if (confirmed.Policy?.Hash != state.Policy!.Hash || confirmed.CardUrl != inlineCardUrl)
        {
            throw new InvalidDataException("Card publication was not confirmed by the actual registered content.");
        }
    }

    public Task PublishDashboardAsync(HaRenderedDashboard rendered, HaPublicationArtifact renderer, CancellationToken cancellationToken = default) =>
        PublishDashboardAsync(rendered.Config, rendered.InputHash, rendered.RenderedNodes, renderer, rendered.CardUrl, cancellationToken);

    public async Task PublishDashboardAsync(JsonObject rendered, string inputHash, IReadOnlyCollection<string> renderedNodes,
        HaPublicationArtifact renderer, string renderedCardUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderedCardUrl);
        using var transaction = await BeginAsync(cancellationToken);
        if (!HaPublicationPolicy.IsHash(inputHash)) { throw new ArgumentException("Invalid input digest.", nameof(inputHash)); }
        if (renderedNodes.Any(n => n.Length == 0 || n.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
        {
            throw new ArgumentException("Invalid rendered-node set.", nameof(renderedNodes));
        }
        var state = await FenceAsync(HaPublicationComponent.Render, renderer, cancellationToken, renderedCardUrl);
        var config = (JsonObject)rendered.DeepClone();
        var policy = state.Policy!;
        config["agent_bridge_publication"] = new JsonObject
        {
            ["protocol"] = 1, ["authority"] = policy.Authority, ["generation"] = policy.Generation, ["writer"] = policy.Writer,
            ["render"] = renderer.ToJson(), ["inputHash"] = inputHash, ["cardUrlHash"] = HaPublicationJson.Hash(state.CardUrl),
            ["renderedNodes"] = new JsonArray([.. renderedNodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n))]),
        };
        config["agent_bridge_publication"]!["contentHash"] = HaPublicationJson.Hash(HaPublicationJson.Canonical(config));
        if (!state.DashboardExists)
        {
            await ha.ApplyPublicationAsync(new(new JsonObject
            {
                ["type"] = "lovelace/dashboards/create", ["url_path"] = dashboard, ["title"] = "Agent Sessions",
                ["icon"] = "mdi:robot", ["show_in_sidebar"] = true, ["require_admin"] = false,
            }), cancellationToken);
        }
        // Check the actual fence and card again after any dashboard registration.
        var current = await ReadAsync(cancellationToken);
        Authorize(current, HaPublicationComponent.Render, renderer);
        if (current.Policy!.Hash != policy.Hash || current.CardUrl != state.CardUrl) { throw new InvalidDataException("Shared publication changed; retry rendering the actual accepted state."); }
        await ha.ApplyPublicationAsync(new(new JsonObject { ["type"] = "lovelace/config/save", ["url_path"] = dashboard, ["config"] = config }), cancellationToken);
        var confirmed = await ReadAsync(cancellationToken);
        var proof = HaVerifiedPublication.Read(confirmed);
        if (!proof.Verified || proof.InputHash != inputHash || !proof.RenderedNodes.SetEquals(renderedNodes) || confirmed.Policy!.Hash != policy.Hash)
        {
            throw new InvalidDataException("Dashboard publication was not confirmed by its actual stored content.");
        }
    }
}
