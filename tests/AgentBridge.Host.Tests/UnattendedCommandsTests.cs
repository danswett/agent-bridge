using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class UnattendedCommandsTests
{
    const string Password = "correct-horse-battery-staple";

    sealed class FakeSystem : IUnattendedSystem
    {
        public bool IsElevated { get; set; } = true;
        public string? Blocked { get; set; }
        public bool Admin { get; set; }
        public string? Secret;
        public readonly Dictionary<string, string> Winlogon = [];
        public readonly HashSet<string> Rights = [];

        public string? AutoLogonBlockedReason() => Blocked;
        public bool IsAdministrator(string account) => Admin;
        public void StorePassword(string? password) => Secret = password;
        public string? GetWinlogon(string name) => Winlogon.GetValueOrDefault(name);
        public void SetWinlogon(string name, string value) => Winlogon[name] = value;
        public void DeleteWinlogon(string name) => Winlogon.Remove(name);
        public void AddRight(string account, string right) => Rights.Add($"{account}:{right}");
        public void RemoveRight(string account, string right) => Rights.Remove($"{account}:{right}");
    }

    static (int Code, string Text) Run(FakeSystem system, params string[] args)
    {
        var output = new StringWriter();
        var code = UnattendedCommands.Run(args, system, () => Password, output, output);
        return (code, output.ToString());
    }

    static FakeSystem SetUp()
    {
        var system = new FakeSystem();
        Assert.Equal(0, Run(system, "setup-unattended", "--user", @"VM\hostuser").Code);
        return system;
    }

    [Fact]
    public void Setup_keeps_the_password_only_as_the_secret_and_never_prints_it()
    {
        var system = new FakeSystem { Winlogon = { ["DefaultPassword"] = "plain" } };
        var (code, text) = Run(system, "setup-unattended", "--user", @"VM\hostuser");
        Assert.Equal(0, code);
        Assert.Equal(Password, system.Secret);
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain(system.Winlogon, w => w.Key == "DefaultPassword" || w.Value.Contains(Password));
        Assert.Equal("1", system.Winlogon["AutoAdminLogon"]);
        Assert.Equal("hostuser", system.Winlogon["DefaultUserName"]);
        Assert.Contains(@"VM\hostuser:SeDenyRemoteInteractiveLogonRight", system.Rights);
        Assert.Contains(@"VM\hostuser:SeDenyNetworkLogonRight", system.Rights);
    }

    [Theory]
    [InlineData("not elevated")]
    [InlineData("blocked by policy")]
    [InlineData("administrator")]
    public void Setup_refuses_and_changes_nothing_when_it_cannot_be_done_safely(string reason)
    {
        var system = new FakeSystem { IsElevated = reason != "not elevated", Blocked = reason == "blocked by policy" ? "Windows Hello-only sign-in is on" : null, Admin = reason == "administrator" };
        var (code, text) = Run(system, "setup-unattended", "--user", @"VM\hostuser");
        Assert.Equal(1, code);
        Assert.Null(system.Secret);
        Assert.Empty(system.Winlogon);
        Assert.Empty(system.Rights);
        if (reason == "blocked by policy") { Assert.Contains("Windows Hello-only", text); }
    }

    [Fact]
    public void Disable_leaves_no_secret_auto_logon_value_or_logon_right()
    {
        var system = SetUp();
        system.Winlogon["DefaultPassword"] = "added by hand";
        var (code, text) = Run(system, "disable-unattended");
        Assert.Equal(0, code);
        Assert.Null(system.Secret);
        Assert.Empty(system.Winlogon);
        Assert.Empty(system.Rights);
        Assert.DoesNotContain(Password, text);
    }

    [Fact]
    public void Disable_can_be_run_again_and_names_the_account_when_none_was_recorded()
    {
        var system = SetUp();
        Run(system, "disable-unattended");
        system.Rights.Add(@"VM\hostuser:SeInteractiveLogonRight");
        Assert.Equal(0, Run(system, "disable-unattended", "--user", @"VM\hostuser").Code);
        Assert.Empty(system.Rights);
        Assert.Null(system.Secret);
    }

    [Fact]
    public void Disable_refuses_without_elevation_and_leaves_everything_as_it_was()
    {
        var system = SetUp();
        system.IsElevated = false;
        Assert.Equal(1, Run(system, "disable-unattended").Code);
        Assert.Equal(Password, system.Secret);
    }
}
