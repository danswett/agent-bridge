namespace AgentBridge.Protocol;

// The product's user-facing names, the one place the Windows app, the host and the launcher read them
// from (#537). The name itself is the brand token (design/tokens.json brand, generated into
// DesignBrand); this adds the phrases built on it that the copy uses (docs/design/brand.md §3.1).
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
    public const string Name = DesignBrand.Name;

    // The Windows app, as the host's messages and the app's own copy name it ("the Slipgates app").
    public const string App = Name + " app";

    // The host service on each machine (brand.md N1): "the Slipgates host" in every message, since
    // each can be read on its own. The Settings section that is already about it says "Host".
    public const string HostName = Name + " host";

    // What this machine's app is called on the person's other devices, their machine lists and push
    // devices (brand.md N5: "{device} · Slipgates" on every client). The relay takes it afresh on
    // each connection, so a renamed product renames every existing device too; the device's
    // identity is its key, never this name (FleetTests).
    public static string DeviceName(string machine) => $"{machine} · {Name}";
}
