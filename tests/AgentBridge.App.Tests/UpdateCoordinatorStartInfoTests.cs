using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateCoordinatorStartInfoTests
{
    [Fact]
    public void Launch_specs_preserve_verified_package_and_profile_arguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "coordinator launch");
        var packages = Path.Combine(root, "packages");
        var package = Path.Combine(root, "handoff", "verified.nupkg");
        var request = new UpdateCoordinatorRequest(root, packages, package,
            new(PackageDigest.Sha256, new string('A', 64)), "AgentBridgeApp", "0.1.200",
            ["--background", "--profile", "two words"], 11, 123);

        var apply = UpdateCoordinatorStartInfo.ApplyStart(request);
        var app = UpdateCoordinatorStartInfo.AppStart(request);

        Assert.Equal(Path.Combine(root, "Update.exe"), apply.FileName);
        Assert.Equal(root, apply.WorkingDirectory);
        Assert.False(apply.UseShellExecute);
        Assert.True(apply.CreateNoWindow);
        Assert.Equal(["--silent", "apply", "--norestart", "--package", package,
            "--rootDir", root, "--packageDir", packages], apply.ArgumentList);
        Assert.Equal(Path.Combine(root, "current", "AgentBridge.exe"), app.FileName);
        Assert.Equal(["--background", "--profile", "two words"], app.ArgumentList);
        Assert.Equal(Path.Combine(root, "current"), app.WorkingDirectory);
        Assert.False(app.UseShellExecute);
        Assert.True(app.CreateNoWindow);
    }
}
