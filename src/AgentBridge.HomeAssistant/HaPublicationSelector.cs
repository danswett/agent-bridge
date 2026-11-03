using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

internal sealed partial class HaPublicationWriter
{
    const string SelectorId = "agent_bridge_target_machine";
    const string SelectorEntity = "input_select." + SelectorId;
    const string SelectorName = "Agent bridge target machine";

    public async Task<string> ReconcileSelectorAsync(IReadOnlyCollection<HaFleetMachine> machines,
        HaPublicationArtifact renderer, CancellationToken ct = default)
    {
        using var transaction = await BeginAsync(ct);
        var state = await FenceAsync(HaPublicationComponent.Render, renderer, ct);
        var policyHash = state.Policy!.Hash;
        var options = machines.Where(m => m.Online).OrderBy(m => m.Slug, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => m.Machine).Distinct(StringComparer.Ordinal).ToArray();
        var helpers = await SelectorsAsync(ct);
        var existing = helpers.SingleOrDefault(h => Id(h) == SelectorId);
        async Task Check()
        {
            var actual = await ReadAsync(ct);
            Authorize(actual, HaPublicationComponent.Render, renderer);
            if (actual.Policy!.Hash != policyHash) { throw new InvalidDataException("Selector publication fence changed."); }
        }
        async Task<JsonElement> Write(JsonObject command)
        {
            await Check();
            return await ha.ApplyPublicationAsync(new(command), ct);
        }
        if (options.Length < 2)
        {
            if (existing.ValueKind != JsonValueKind.Undefined)
            {
                await Write(new JsonObject { ["type"] = "input_select/delete", ["input_select_id"] = SelectorId });
                if ((await SelectorsAsync(ct)).Any(h => Id(h) == SelectorId)) { throw new InvalidDataException("Selector removal was not confirmed."); }
            }
            return "";
        }
        if (existing.ValueKind == JsonValueKind.Undefined)
        {
            if (helpers.Any(h => h.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == SelectorName) ||
                (await ha.ListEntityRegistryAsync(ct)).Any(e => e.EntityId == SelectorEntity))
            {
                throw new InvalidDataException("Machine selector identity is occupied; refusing to create duplicate controls.");
            }
            var created = await Write(Command("input_select/create", options));
            var createdId = Id(created);
            if (createdId != SelectorId)
            {
                // Remove only the new, specifically returned duplicate. Never infer
                // ownership from a display name or delete a pre-existing helper.
                if (createdId.StartsWith(SelectorId + "_", StringComparison.Ordinal) && helpers.All(h => Id(h) != createdId))
                {
                    await Write(new JsonObject { ["type"] = "input_select/delete", ["input_select_id"] = createdId });
                }
                throw new InvalidDataException("Home Assistant did not give the selector its exact compatible id.");
            }
        }
        else if (!Options(existing).SequenceEqual(options, StringComparer.Ordinal))
        {
            var update = Command("input_select/update", options);
            update["input_select_id"] = SelectorId;
            await Write(update);
        }
        var confirmed = (await SelectorsAsync(ct)).SingleOrDefault(h => Id(h) == SelectorId);
        if (confirmed.ValueKind == JsonValueKind.Undefined || !Options(confirmed).SequenceEqual(options, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Selector options were not confirmed by actual inventory.");
        }
        var selection = await ha.GetStateAsync(SelectorEntity, ct);
        if (selection is null || !options.Contains(selection.State, StringComparer.Ordinal))
        {
            await Check();
            await ha.CallServiceAsync("input_select", "select_option", new JsonObject { ["entity_id"] = SelectorEntity, ["option"] = options[0] }, ct);
            selection = await ha.GetStateAsync(SelectorEntity, ct);
            if (selection is null || !options.Contains(selection.State, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Machine selector repair was not confirmed; retry after Home Assistant settles.");
            }
        }
        return SelectorEntity;
    }

    static JsonObject Command(string type, string[] options) => new()
    {
        ["type"] = type, ["name"] = SelectorName, ["icon"] = "mdi:desktop-tower-monitor",
        ["options"] = new JsonArray([.. options.Select(s => JsonValue.Create(s))]),
    };

    async Task<JsonElement[]> SelectorsAsync(CancellationToken ct)
    {
        var inventory = await ha.ReadMachineSelectorsAsync(ct);
        if (inventory.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("Malformed machine-selector inventory."); }
        var entries = inventory.EnumerateArray().ToArray();
        if (entries.Select(Id).Distinct(StringComparer.Ordinal).Count() != entries.Length) { throw new InvalidDataException("Duplicate machine-selector registrations."); }
        return entries;
    }

    static string Id(JsonElement helper)
    {
        if (helper.ValueKind != JsonValueKind.Object ||
            helper.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != helper.EnumerateObject().Count() ||
            !helper.TryGetProperty("id", out var value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } id ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new InvalidDataException("Malformed machine-selector identity.");
        }
        return id;
    }

    static string[] Options(JsonElement helper)
    {
        if (!helper.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array ||
            options.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException("Malformed machine-selector options.");
        }
        return options.EnumerateArray().Select(v => v.GetString()!).ToArray();
    }
}
