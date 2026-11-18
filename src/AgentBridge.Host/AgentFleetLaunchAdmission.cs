using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal sealed record AgentFleetLaunchPermit(LaunchRequest Request, string Grant);

internal static class AgentFleetLaunchAdmission
{
    internal static AgentFleetLaunchPermit Admit(string folder, TimeProvider time, JsonElement data,
        AgentAuditLog? audit, string origin, Func<string?> live)
    {
        LaunchRequest? request = null;
        AgentFleetLaunchGrantState? state = null;
        string? reason = live();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())));
        try
        {
            if (data.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(data.GetRawText()) > LaunchProtocol.MaxRequest
                || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                || data.EnumerateObject().Any(p => p.Name is not ("request" or "agent" or "workspace" or "permissions"
                    or "model" or "effort" or "context" or "prompt" or "role" or "fleetName")))
            { reason ??= "fleet launch contains unsupported or duplicate fields or exceeds its size limit"; }
            else
            {
                request = data.Deserialize<LaunchRequest>(ProtocolJson.Options);
                if (request is null || !Guid.TryParseExact(request.Request, "D", out var id) || id == Guid.Empty)
                { reason ??= "fleet launch request must name a nonempty UUID"; }
                else { key = LaunchService.RequestBinding(request); }
            }
        }
        catch (JsonException) { reason ??= "fleet launch request has invalid field shapes"; }
        try { state = AgentFleetLaunchGrants.Read(folder, time); }
        catch (InvalidOperationException) { reason ??= "the fleet-launch grant could not be read safely"; }
        if (state?.State != "active") { reason ??= $"fleet-launch grant is {state?.State ?? "unreadable"}; no fallback"; }
        if (reason is null && request is not null)
        {
            // Claims are permanent, including when audit or dispatch fails after reservation.
            try
            {
                var claims = PrivateFolder.Ensure(Path.Combine(folder, "fleet-launch-requests"));
                var path = Path.Combine(claims, Guid.Parse(request.Request).ToString("N") + ".json");
                var binding = JsonSerializer.Serialize(new { grant = state!.Grant!.Id, origin, key });
                string? previous = null;
                try { previous = File.ReadAllText(path); }
                catch (FileNotFoundException) { }
                if (previous is not null)
                { reason = previous == binding ? "fleet launch replay is refused" : "fleet launch request key conflicts with its durable binding"; }
                else if (Check(folder, time, request, state!.Grant!.Id) is { } denied) { reason = denied; }
                else
                {
                    using var stream = PrivateFolder.CreateNew(path);
                    stream.Write(Encoding.UTF8.GetBytes(binding));
                    stream.Flush(flushToDisk: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { reason = "fleet launch request key is already claimed or its durable state is unreadable"; }
        }
        var requestId = request is not null && Guid.TryParseExact(request.Request, "D", out var uuid)
            ? uuid.ToString("D") : Guid.NewGuid().ToString("D");
        if (audit is null || !audit.Append(new("local", requestId, "fleet-launcher", [origin],
            "fleet.launch.attempt", key, reason is null ? "reserved" : "refused", reason ?? "")
            { Grant = state?.Grant?.Id }))
        { throw new InvalidOperationException("refused: the fleet launch audit could not be written durably"); }
        if (reason is not null) { throw new InvalidOperationException("refused: " + reason); }
        return new(request!, state!.Grant!.Id);
    }

    internal static string? Check(string folder, TimeProvider time, LaunchRequest request, string grant)
    {
        try
        {
            var current = AgentFleetLaunchGrants.Read(folder, time);
            if (current.State != "active" || current.Grant?.Id != grant)
            { return "the exact fleet-launch grant is no longer active"; }
            var (settings, problem) = HostSettings.Load(folder);
            if (problem is not null || settings.SectionProblems.Any()) { return "cap-state: fleet launch settings cannot be read safely"; }
            if (!settings.Agents.Enabled) { return "agents.enabled: fleet launches are off"; }
            if (!settings.Launch.Enabled) { return "launch.enabled: launching is disabled"; }
            if (request.Role is null || !settings.Launch.WorkerMcp.ContainsKey(request.Role))
            { return "only an exact configured fleet role may launch"; }
            if (request.FleetName is null || !Fleet.FleetNames.IsValidName(request.FleetName))
            { return "fleet launch requires a valid fleet name"; }
            var workspace = settings.Launch.Workspaces.SingleOrDefault(w => w.Label == request.Workspace);
            if (workspace?.Isolate != true || !settings.Agents.FleetWorkspaces.Contains(workspace.Label, StringComparer.Ordinal))
            { return "fleet launch requires an explicitly approved isolated workspace"; }
            return request.Agent is null || request.Permissions is not (LaunchProtocol.Ask or LaunchProtocol.AllowAll)
                || settings.Launch.Agent(request.Agent).ExtraArgs.Count != 0 || LaunchProtocol.Check(request) is not null
                ? "fleet launch request has invalid fields or options" : null;
        }
        catch (InvalidOperationException) { return "the fleet-launch grant or settings could not be read safely"; }
    }
}
