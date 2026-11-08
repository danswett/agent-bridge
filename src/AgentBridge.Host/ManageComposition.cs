using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Remote management (#801) as the host composes it: the switch and the restart's eligibility read
// from the host's settings, one quiet lease for every departure (#805), and the service itself.
// Program builds it here so a test can build the very same thing: with the composition inline,
// wiring the switch to always-on (sr2-886d's M5) compiled and passed every management test.
//
// Update now (#1159) leaves through the same lease, as purpose Update, and its conditions are
// registered here, once: the remote restart's eligibility (not read-only, permissions.remoteManage
// on, no #800 sign-in entrypoint on) and no other profile's host running from this installation.
// A purpose can be registered only once, so this is the Update registration every other Update
// consumer (#819's, #870) must add its conditions to, not register beside.
public sealed record ManageComposition(ManageService Manage, LifecycleCoordinator Lifecycle, RemoteRestartEligibility Eligibility, OtherProfileHosts OtherHosts)
{
    public static ManageComposition For(HostSettings settings, P256KeyPair key, string folder, DeliveryGate delivery,
        Func<string?> readOnly, Func<string?>? offeredReadOnly, Action restartHost, IManageNotices? notices, string machineName,
        Action<string>? log = null, TimeProvider? time = null, DepartureWatchdog? watchdog = null, Func<string?>? otherHosts = null)
    {
        // A remote restart commits only while the machine is still eligible: not read-only, the
        // switch on, and no #800 sign-in it could cut off (SessionService binds that when given
        // manage; Connection's Q-C8 on #886). Refreshed by ManageService outside the fence;
        // readOnly is the acting answer (G5's Active).
        var eligibility = RemoteRestartEligibility.For(settings, readOnly, time);
        // Another profile's host from this installation (HostUpdateGate.OtherHosts where the host is
        // packaged); none where nothing else can run from the package.
        var others = new OtherProfileHosts(otherHosts ?? (() => null), time);
        var departures = new DepartureConditions();
        departures.Register(DeparturePurpose.RemoteRestart, eligibility);
        departures.Register(DeparturePurpose.Update, eligibility, others);
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
        return new(manage, lifecycle, eligibility, others);
    }

    // What UpdateService needs for Update now: this composition's gate, caps, lease and conditions,
    // with the sessions its busy is read from and the folder its record of presses is kept in.
    public UpdateNowParts UpdateNow(ISessionSource sessions, string folder) => new(Manage, Lifecycle, Eligibility, OtherHosts, sessions, folder);
}

public sealed record UpdateNowParts(ManageService Manage, LifecycleCoordinator Lifecycle, RemoteRestartEligibility Eligibility, OtherProfileHosts OtherHosts,
    ISessionSource Sessions, string Folder);