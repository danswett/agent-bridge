namespace AgentBridge.Local;

// Between agent-bridge-hostw (the logon task's launcher) and the host it runs (#106). Compiled
// into both, so neither can drift: the launcher has no other dependency, and keeps its copy to
// itself.
#if AGENT_BRIDGE_HOSTW
internal
#else
public
#endif
static class HostLauncherContract
{
    // Set to "1" by the launcher for the host it starts. The host clears it at once, so nothing it
    // starts in turn (an agent, a replacement) inherits it.
    public const string Variable = "AGENT_BRIDGE_HOSTW";

    // A host under the launcher ends with this after host.restart, once fully stopped: the
    // launcher starts it again at once, still watching it, rather than the host starting a
    // replacement of its own that nothing watches. A small code, so never mistaken for a crash.
    public const int Restart = 75;
}
