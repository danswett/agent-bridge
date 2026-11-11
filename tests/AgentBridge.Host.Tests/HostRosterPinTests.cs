using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Xunit;

public sealed class HostRosterPinTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "roster-pin-" + Guid.NewGuid().ToString("N"));
    string PathName => Path.Combine(folder, "roster-pin.json");
    readonly World w = new();

    public HostRosterPinTests()
    {
        PrivateFolder.Ensure(folder);
        w.Genesis();
        w.Add("B", "G", "B", "G");
    }

    RelayRoster Relay(params string[] labels) => new("observe", w.GenerationId, [.. labels.Select(l => w.Entry(l).Canonical())]);
    void Pin(string? account = null, string? generation = null, string? genesis = null, params string[] heads) =>
        Write(JsonSerializer.Serialize(new
        {
            account = account ?? RosterFixture.Account, generation = generation ?? RosterFixture.Generation,
            genesis = genesis ?? Codec.Base64Url(w.Entry("G").Hash),
            heads = heads.Length == 0 ? [Codec.Base64Url(w.Entry("B").Hash)] : heads,
        }));
    void Write(string text)
    {
        using var stream = PrivateFolder.CreateNew(PathName);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    [Fact]
    public void Missing_pin_never_creates_trust_or_a_file()
    {
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Contains("unpinned", reader.Update(Relay("G", "B"), RosterFixture.Account).Reasons);
        Assert.Null(reader.Verified);
        Assert.False(File.Exists(PathName));
    }

    [Fact]
    public void Matching_pin_retains_the_verified_reader_without_changing_the_pin()
    {
        Pin();
        var before = File.ReadAllBytes(PathName);
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("verified", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        var verified = reader.Verified;
        w.Add("C", "G", "C", "B");
        Assert.Equal("verified", reader.Update(Relay("G", "B", "C"), RosterFixture.Account).Outcome);
        Assert.Same(verified, reader.Verified);
        Assert.True(reader.Verified!.Serving.ContainsKey(w.Id("C")));
        Assert.Equal("head-regress", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        Assert.True(reader.Verified.Serving.ContainsKey(w.Id("C")));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("generation")]
    [InlineData("genesis")]
    public void A_different_pin_identity_never_becomes_a_verified_reader(string field)
    {
        Pin(account: field == "account" ? "another-account" : null,
            generation: field == "generation" ? new string('f', 32) : null,
            genesis: field == "genesis" ? Codec.Base64Url(new byte[32]) : null);
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("pin-mismatch", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        Assert.Null(reader.Verified);
    }

    [Fact]
    public void A_regression_or_failed_sync_keeps_the_last_verified_reader()
    {
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        reader.Update(Relay("G", "B"), RosterFixture.Account);
        var verified = reader.Verified;
        Assert.Equal("head-regress", reader.Update(Relay("G"), RosterFixture.Account).Outcome);
        Assert.Equal("no-roster", reader.Update(null, RosterFixture.Account).Outcome);
        Assert.Equal("pin-mismatch", reader.Update(Relay("G", "B"), "another-account").Outcome);
        Assert.Same(verified, reader.Verified);
        Assert.True(reader.Verified!.Serving.ContainsKey(w.Id("B")));
    }

    [Fact]
    public void Repeated_sync_does_not_restart_the_verifiers_probation_clock()
    {
        Pin();
        w.Add("C", "B", "C", "B");
        var reader = new HostRosterReader(PathName, () => w.Now);
        reader.Update(Relay("G", "B", "C"), RosterFixture.Account);
        Assert.False(reader.Verified!.Serving.ContainsKey(w.Id("C")));
        w.Now = TimeSpan.FromHours(73);
        reader.Update(Relay("G", "B", "C"), RosterFixture.Account);
        Assert.True(reader.Verified.Serving.ContainsKey(w.Id("C")));
    }

    [Theory]
    [InlineData("{broken: secret-marker}")]
    [InlineData("{}")]
    [InlineData("{\"account\":\"a\",\"account\":\"b\"}")]
    [InlineData("[]")]
    public void Damaged_pin_reports_only_a_fixed_content_free_problem(string text)
    {
        Write(text);
        var reader = new HostRosterReader(PathName, () => w.Now);
        var check = reader.Update(Relay("G", "B"), RosterFixture.Account);
        Assert.Equal("pin-unreadable", check.Outcome);
        Assert.DoesNotContain("secret-marker", check.Summary);
        Assert.Null(reader.Verified);
    }

    [Fact]
    public void Oversize_or_locked_pin_is_not_treated_as_missing()
    {
        Write(new string(' ', 4097));
        Assert.Equal("pin-unreadable", new HostRosterReader(PathName, () => w.Now).Update(Relay("G"), RosterFixture.Account).Outcome);
        File.Delete(PathName);
        Pin();
        using var locked = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal("pin-unreadable", new HostRosterReader(PathName, () => w.Now).Update(Relay("G"), RosterFixture.Account).Outcome);
    }

    [Fact]
    public void A_valid_but_public_pin_is_not_trusted()
    {
        Pin();
        if (OperatingSystem.IsWindows())
        {
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(PathName).SetAccessControl(acl);
        }
        else { File.SetUnixFileMode(PathName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); }
        Assert.Equal("pin-unreadable", new HostRosterReader(PathName, () => w.Now).Update(Relay("G", "B"), RosterFixture.Account).Outcome);
    }

    public void Dispose()
    {
        File.Delete(PathName);
        Directory.Delete(folder);
    }
}
