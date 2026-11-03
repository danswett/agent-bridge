using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;

internal sealed partial class FakeHomeAssistant
{
    public string? Refused { get; set; }
    public List<JsonObject> PublicationWrites { get; } = [];
    public Action<JsonObject>? PublicationWrite { get; set; }
    public Func<JsonObject, Task>? BeforePublicationWrite { get; set; }
    public JsonElement MachineSelectors { get; set; } = JsonSerializer.SerializeToElement(Array.Empty<object>());
    public Func<string, string>? SelectorCreatedId { get; set; }
    public Task<JsonElement> ReadMachineSelectorsAsync(CancellationToken cancellationToken = default) => Task.FromResult(MachineSelectors);

    public async Task<JsonElement> ApplyPublicationAsync(HaPublicationMutation mutation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = mutation.Command();
        if (BeforePublicationWrite is { } before) { await before(command); }
        PublicationWrites.Add((JsonObject)command.DeepClone());
        if (PublicationWrite is { } apply) { apply(command); return JsonSerializer.SerializeToElement<object?>(null); }
        JsonNode? result = null;
        var type = command["type"]!.GetValue<string>();
        if (type is "lovelace/resources/create" or "lovelace/resources/update")
        {
            var resources = JsonNode.Parse(Resources.GetRawText())!.AsArray();
            var id = command["resource_id"]?.GetValue<string>();
            var entry = id is null ? new JsonObject { ["id"] = "resource-" + (resources.Count + 1) }
                : resources.OfType<JsonObject>().Single(r => r["id"]!.GetValue<string>() == id);
            entry["url"] = command["url"]!.DeepClone();
            entry["type"] = command["res_type"]!.DeepClone();
            if (id is null) { resources.Add(entry); }
            Resources = JsonSerializer.SerializeToElement(resources);
        }
        else if (type == "lovelace/dashboards/create")
        {
            var dashboards = JsonNode.Parse(Dashboards.GetRawText())!.AsArray();
            dashboards.Add(new JsonObject { ["id"] = "dashboard-" + (dashboards.Count + 1), ["url_path"] = command["url_path"]!.DeepClone() });
            Dashboards = JsonSerializer.SerializeToElement(dashboards);
        }
        else if (type == "lovelace/config/save")
        {
            Dashboard = command["config"]!.ToJsonString();
        }
        else if (type.StartsWith("input_select/", StringComparison.Ordinal))
        {
            var selectors = JsonNode.Parse(MachineSelectors.GetRawText())!.AsArray();
            var id = command["input_select_id"]?.GetValue<string>() ?? SelectorCreatedId?.Invoke("agent_bridge_target_machine") ?? "agent_bridge_target_machine";
            if (type == "input_select/delete")
            {
                var existing = selectors.OfType<JsonObject>().Single(s => s["id"]!.GetValue<string>() == id);
                selectors.Remove(existing);
                States.Remove("input_select." + id);
            }
            else
            {
                var entry = type == "input_select/create" ? new JsonObject { ["id"] = id }
                    : selectors.OfType<JsonObject>().Single(s => s["id"]!.GetValue<string>() == id);
                entry["name"] = command["name"]!.DeepClone();
                entry["icon"] = command["icon"]!.DeepClone();
                entry["options"] = command["options"]!.DeepClone();
                if (type == "input_select/create") { selectors.Add(entry); }
                result = entry.DeepClone();
                States["input_select." + id] = new("input_select." + id, "unknown", JsonSerializer.SerializeToElement(entry));
            }
            MachineSelectors = JsonSerializer.SerializeToElement(selectors);
        }
        else { throw new InvalidOperationException("Unexpected publication command: " + type); }
        return JsonSerializer.SerializeToElement(result);
    }
}
