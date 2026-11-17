using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public sealed partial class SessionCardViewModel
{
    string fleetNameDraft = "";
    string fleetNameResult = "";
    bool savingFleetName;

    public string FleetNameDisclosure => FleetNamePresentation.Disclosure;
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
            FleetNameResult = FleetNamePresentation.Saved(result);
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
