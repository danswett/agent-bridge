namespace AgentBridge.Local;

#if AGENT_BRIDGE_HOSTW
internal
#else
public
#endif
static class UpdateVersions
{
    public static string? Identity(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return null; }
        var release = version.Trim();
        if (release[0] is 'v' or 'V') { release = release[1..]; }
        var metadata = release.IndexOf('+');
        if (metadata >= 0) { release = release[..metadata]; }
        return release is "" or "0.0.0" ? null : release;
    }
}
