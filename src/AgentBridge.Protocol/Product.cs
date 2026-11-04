namespace AgentBridge.Protocol;

// The product's user-facing name, the one place the Windows app, the host and the launcher read it
// from (#537). It was typed by hand in about 25 places, so renaming it (#535) meant a hunt through
// copy and the tests that pinned it; now the rename is a change here.
//
// Only what a person reads comes from here. Identifiers that happen to contain the name stay as
// they are, because installed copies, other clients and the owner's automations find things by
// them (#537's "Do not rename in Phase 1"; #541): namespaces and assembly names, the
// toast AUMID and activator, the Velopack pack id, exe names, the mutex, the logon task's name,
// the pipe, data and config paths, environment variables, Home Assistant ids and names, and
// protocol labels.
//
// Compiled into agent-bridge-hostw too (a linked file), which keeps to one small assembly.
#if AGENT_BRIDGE_HOSTW
internal
#else
public
#endif
static class Product
{
    public const string Name = "Agent Bridge";

    // The Windows app, as the host's messages and the app's own copy name it ("the Agent Bridge app").
    public const string App = Name + " app";

    // The host service on each machine, in the app's status and error copy. A term of its own today;
    // the brand spec (#536, naming decision N1) decides what it becomes.
    public const string HostName = "Bridge Host";

    // What this machine's app is called on the person's other devices (their machine lists and
    // push devices). Only new connections take it: a device already known keeps the name it had.
    public static string DeviceName(string machine) => $"{machine} · {App}";
}
