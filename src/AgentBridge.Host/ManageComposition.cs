using AgentBridge.Host.Delivery;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Remote management (#801) as the host composes it: the switch and the restart's eligibility read
// from the host's settings, one quiet lease for every departure (#805), and the service itself.
// Program builds it here so a test can build the very same thing: with the composition inline,
// wiring the switch to always-on (sr2-886d's M5) compiled and passed every management test.
public sealed record ManageComposition(ManageService Manage, LifecycleCoordinator Lifecycle, RemoteRestartEligibility Eligibility)
{
    public static ManageComposition For(HostSettings settings, P256KeyPair key, string folder, DeliveryGate delivery,
        Func<string?> readOnly, Func<string?>? offeredReadOnly, Action restartHost, IManageNotices? notices, string machineName,
        Action<string>? log = null, TimeProvider? time = null, DepartureWatchdog? watchdog = null)
    {
        // A remote restart commits only while the machine is still eligible: not read-only, the
        // switch on, and no #800 sign-in it could cut off (SessionService binds that when given
        // manage; Connection's Q-C8 on #886). Refreshed by ManageService outside the fence;
        // readOnly is the acting answer (G5's Active).
        var eligibility = RemoteRestartEligibility.For(settings, readOnly, time);
        var departures = new DepartureConditions();
        departures.Register(DeparturePurpose.RemoteRestart, eligibility);
        var lifecycle = new LifecycleCoordinator(delivery, departures, time: time, watchdog: watchdog);
        var manage = new ManageService(key, folder, log, time)
        {
            Settings = settings,
            ReadOnly = readOnly,
            OfferedReadOnly = offeredReadOnly,
            Lifecycle = lifecycle,
            Eligibility = eligibility,
            RestartHost = restartHost,
            Notices = notices,
            MachineName = machineName,
        };
        return new(manage, lifecycle, eligibility);
    }
}