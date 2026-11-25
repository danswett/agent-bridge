using Xunit;

namespace AgentBridge.Transport.Tests;

// #4520: AGENT_BRIDGE_PROFILE_ROOT, the one way a test or a diagnostic moves the profile root. These
// check the rule on its value; ProfileRootIsolationTests in the host tests prove a real process obeys it.
public sealed class ProfileRootTests
{
    static string Absolute(params string[] parts) => Path.Combine([Path.GetTempPath(), "profile-root-tests", .. parts]);

    [Fact]
    public void An_absolute_root_is_the_root()
    {
        var root = Absolute("a");
        Assert.Equal(root, ProfileFolders.ResolveRoot(root));
        Assert.Equal(root, ProfileFolders.ResolveRoot("  " + root + Path.DirectorySeparatorChar));
        Assert.Equal(root, ProfileFolders.ResolveRoot(Path.Combine(root, "b", "..")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_empty_variable_is_the_product_root_and_no_refusal(string? value)
    {
        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");
        Assert.Equal(expected, ProfileFolders.ResolveRoot(value));
        Assert.Null(ProfileFolders.RefusalFor(value));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("relative/root")]
    [InlineData("..")]
    [InlineData("./x")]
    public void A_relative_root_is_refused_and_never_replaced_by_the_product_root(string value)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ProfileFolders.ResolveRoot(value));
        Assert.Equal(ProfileFolders.RootRefusal, refused.Message);
        Assert.Equal(ProfileFolders.RootRefusal, ProfileFolders.RefusalFor(value));
        Assert.DoesNotContain(value, refused.Message);
    }
}
