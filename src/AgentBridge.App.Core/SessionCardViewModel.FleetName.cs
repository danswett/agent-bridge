using System.Text.Json;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

public sealed partial class SessionCardViewModel
{
    string fleetNameDraft = "";
    string fleetNameResult = "";
    bool savingFleetName;

    public string FleetNameDisclosure => "The relay sees this label. Choose a public-safe label, such as a role name. "
        + "Do not include private task details, account names, email addresses, repository names or paths. Blank clears the label.";
    public string FleetNameDraft { get => fleetNameDraft; set => Set(ref fleetNameDraft, value); }
    public string FleetNameResult { get => fleetNameResult; private set => Set(ref fleetNameResult, value); }
    public bool ShowFleetEnrollment => ctx.Host is { } host
        && LocalPersonEligibility.CanEnroll(ctx.FleetEnrollmentOrigin, Agent, EnrollmentCaller.App,
            ReadOnlyWhy is not null || host.Status?.ReadOnly is not null || host.Status?.AgentHaBridge is not null
            || host.Hello?.Machine.ReadOnly is not null);
    public bool CanEnrollFleetName => ShowFleetEnrollment && !savingFleetName
        && ctx.Host is { State: LinkState.Connected, IsLocalPipe: true, Status: not null };

    public async Task SaveFleetNameAsync()
    {
        if (savingFleetName) { return; }
        FleetNameResult = "";
        if (!CanEnrollFleetName || ctx.Host is not { } host)
        {
            FleetNameResult = "Fleet labels can only be set in the local, writable App connection.";
            return;
        }
        savingFleetName = true;
        Raise(nameof(CanEnrollFleetName));
        try
        {
            var result = await host.RenameFleetNameAsync(Id, FleetNameDraft);
            var status = result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("duplicateStatus", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            FleetNameResult = status switch
            {
                "present" => "Fleet label saved. Duplicate status: present. Another live session uses this label; lookup may refuse or select a newer session.",
                "absent" => "Fleet label saved. Duplicate status: absent. No duplicate was found; this is not proof of uniqueness.",
                _ => "Fleet label saved. Duplicate status: unknown. Other live labels could not be checked.",
            };
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        {
            FleetNameResult = $"Couldn't save the fleet label: {e.Message}";
        }
        finally
        {
            savingFleetName = false;
            Raise(nameof(CanEnrollFleetName));
        }
    }
}
