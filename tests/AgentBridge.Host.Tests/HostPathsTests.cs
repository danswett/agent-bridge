using AgentBridge.Host;
using Xunit;

public sealed class HostPathsTests
{
    [Fact]
    public void Mac_profiles_never_read_legacy_registration_roots_by_default()
    {
        var root = Path.GetFullPath("isolated-host");
        var paths = HostPaths.Resolve(root, "mac-local", null, macOS: true);
        Assert.Equal(Path.Combine(root, "mac-local"), paths.ProfileFolder);
        Assert.Equal([Path.Combine(root, "mac-local", "registrations", "agent-bridge-claude")], paths.Registrations(true, ["legacy"]));
        Assert.Equal([Path.Combine(root, "mac-local", "registrations", "agent-bridge-codex")], paths.Registrations(false, ["legacy"]));
    }

    [Fact]
    public void Explicit_registration_root_replaces_all_ambient_roots()
    {
        var root = Path.GetFullPath("isolated-host");
        var registrations = Path.GetFullPath("isolated-registrations");
        var paths = HostPaths.Resolve(root, "test", registrations, macOS: false);
        Assert.Equal([Path.Combine(registrations, "agent-bridge-codex")], paths.Registrations(false, ["legacy", "other"]));
        Assert.Equal(["legacy"], HostPaths.Resolve(root, "test", null, macOS: false).Registrations(false, ["legacy"]));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(".")]
    [InlineData("/absolute")]
    public void Profile_cannot_escape_its_explicit_root(string profile) =>
        Assert.Throws<ArgumentException>(() => HostPaths.Resolve(Path.GetFullPath("isolated-host"), profile, null, true));

    [Fact]
    public void Relative_roots_are_refused_before_any_identity_is_created()
    {
        Assert.Throws<ArgumentException>(() => HostPaths.Resolve("relative", "test", null, true));
        Assert.Throws<ArgumentException>(() => HostPaths.Resolve(Path.GetFullPath("isolated-host"), "test", "relative", true));
    }
}
