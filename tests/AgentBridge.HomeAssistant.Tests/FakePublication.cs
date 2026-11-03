using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;

internal sealed partial class FakeHomeAssistant
{
    public List<JsonObject> PublicationWrites { get; } = [];
    public Action<JsonObject>? PublicationWrite { get; set; }

    public Task ApplyPublicationAsync(HaPublicationMutation mutation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = mutation.Command();
        PublicationWrites.Add((JsonObject)command.DeepClone());
        if (PublicationWrite is { } apply) { apply(command); return Task.CompletedTask; }
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
        else { throw new InvalidOperationException("Unexpected publication command: " + type); }
        return Task.CompletedTask;
    }
}
