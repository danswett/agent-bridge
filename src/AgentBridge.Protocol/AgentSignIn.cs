namespace AgentBridge.Protocol;

// Off unless the machine's own permissions.phoneSignIn turns it on (#972), and then only for an
// agent with a registered private adapter: there is no build-time default that enables it.
public static class AgentSignIn
{
    public const string Capability = "sessions.signin";
    public const string Start = "signin.start";
    public const string Code = "signin.code";
    public const string Done = "signin.done";
    public const string Cancel = "signin.cancel";
    // #972 slice 2: who can sign an agent in on this machine, read-only, offered where the machine
    // has the switch on. The same view goes to the app beside the host in its status.
    public const string Devices = "signin.devices";
    public const string DevicesCapability = "signin.devices";
}

// What the machine's person-device roster says (#55): none yet, read, or not readable. Only
// Ready lists anyone; the other two mean nobody can sign an agent in here.
public static class SignInDeviceStates
{
    public const string None = "none";
    public const string Ready = "ready";
    public const string Unreadable = "unreadable";
}

// One device that can sign an agent in here: the roster's name, kind (app, pwa or phone) and
// platform, and whether it is the device asking. Never its id, key, generation or presence.
public sealed record SignInDevice(string Name, string Kind, string Platform, bool? ThisDevice = null);

public sealed record SignInDevices(string State, IReadOnlyList<SignInDevice> Devices);
