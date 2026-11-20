using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Xunit;

// The first-host pin (#3450, brief 182): only a person's out-of-band genesis creates trust.
public sealed class HostRosterPinWriterTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "roster-pin-write-" + Guid.NewGuid().ToString("N"));
    string PinPath => HostRosterPinWriter.PinPath(Path.Combine(root, "default"));
    readonly World w = new();
    int fetches;

    public HostRosterPinWriterTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "default"));
        w.Genesis();
        w.Add("B", "G", "B", "G");
    }

    RelayRoster Relay(string? generation = null) => new("off", generation ?? w.GenerationId, [w.Entry("G").Canonical(), w.Entry("B").Canonical()]);
    string Typed => HostRosterPinWriter.Fingerprint(w.Entry("G").Hash);
    PersonGenesis Source(string? typed = null) => PersonGenesis.FromFingerprint(typed ?? Typed)!;

    async Task<(int Exit, string Out, string Err)> Cli(string verb, string? typed, CallerKind kind = CallerKind.HostCli,
        string? relayAccount = null, bool credential = false)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await RosterPinCommands.RunAsync(["roster-pin", verb], root, () => new Caller(kind, "fixture"), credential,
            typed is null ? null : () => typed,
            (_, _) => { fetches++; return Task.FromResult(new PinFetch(Relay(), RosterFixture.Account, relayAccount ?? RosterFixture.Account)); },
            () => w.Now, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_matching_typed_fingerprint_writes_a_four_field_pin_the_reader_verifies()
    {
        var (exit, output, _) = await Cli("set", Typed.ToUpperInvariant());
        Assert.Equal(0, exit);
        Assert.Contains("\"state\":\"pinned\"", output);
        using var pin = JsonDocument.Parse(File.ReadAllBytes(PinPath));
        Assert.Equal(["account", "generation", "genesis", "heads"], pin.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("verified", new HostRosterReader(PinPath, () => w.Now).Update(Relay(), RosterFixture.Account).Outcome);
        var (shown, status, _) = await Cli("show", null);
        Assert.Equal(0, shown);
        Assert.Contains(Typed, status);
    }

    [Fact]
    public void A_mismatched_fingerprint_writes_nothing()
    {
        var wrong = Typed[..^1] + (Typed[^1] == '0' ? '1' : '0');
        Assert.Equal("fingerprint-mismatch", HostRosterPinWriter.Write(PinPath, Source(wrong), Relay(), RosterFixture.Account, () => w.Now));
        Assert.False(Directory.Exists(Path.GetDirectoryName(PinPath)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(PinPath)!).Any());
    }

    [Fact]
    public async Task An_existing_pin_is_never_overwritten_or_even_fetched_against()
    {
        Assert.Equal("pinned", HostRosterPinWriter.Write(PinPath, Source(), Relay(), RosterFixture.Account, () => w.Now));
        var before = File.ReadAllBytes(PinPath);
        Assert.Equal("pin-exists", HostRosterPinWriter.Write(PinPath, Source(), Relay(), RosterFixture.Account, () => w.Now));
        var (exit, _, error) = await Cli("set", Typed);
        Assert.Equal(1, exit);
        Assert.Contains("pin-exists", error);
        Assert.Equal(0, fetches);
        Assert.Equal(before, File.ReadAllBytes(PinPath));
    }

    [Fact]
    public async Task A_relay_genesis_for_another_account_refuses()
    {
        Assert.Equal("other-account", HostRosterPinWriter.Write(PinPath, Source(), Relay(), "someone-else@example", () => w.Now));
        Assert.Contains("account-mismatch", (await Cli("set", Typed, relayAccount: "someone-else@example")).Err);
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public void Every_ambiguity_refuses_with_a_closed_code_and_writes_nothing()
    {
        Assert.Null(PersonGenesis.FromFingerprint(Typed[..9]));
        Assert.Null(PersonGenesis.FromFingerprint(Typed.Replace(' ', '-')));
        Assert.Null(PersonGenesis.FromFingerprint(Typed + "0"));
        Assert.Equal("no-source", HostRosterPinWriter.Write(PinPath, null, Relay(), RosterFixture.Account, () => w.Now));
        Assert.Equal("no-roster", HostRosterPinWriter.Write(PinPath, Source(), null, RosterFixture.Account, () => w.Now));
        Assert.Equal("generation-mismatch", HostRosterPinWriter.Write(PinPath, Source(), Relay("00"), RosterFixture.Account, () => w.Now));
        Assert.Equal("no-genesis", HostRosterPinWriter.Write(PinPath, Source(), new("off", w.GenerationId, [w.Entry("B").Canonical()]), RosterFixture.Account, () => w.Now));
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public async Task Agents_credentials_and_piped_input_are_refused_before_the_relay_is_asked()
    {
        Assert.Equal(4, (await Cli("set", Typed, CallerKind.Agent)).Exit);
        Assert.Equal(4, (await Cli("set", Typed, CallerKind.App)).Exit);
        Assert.Equal(4, (await Cli("set", Typed, credential: true)).Exit);
        Assert.Contains("not-interactive", (await Cli("set", null)).Err);
        Assert.Equal(0, fetches);
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public void The_pin_writer_is_reachable_only_from_the_local_command_line()
    {
        var src = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(src.FullName, "AgentBridge.slnx"))) { src = src.Parent ?? throw new IOException("repository root not found"); }
        var callers = Directory.EnumerateFiles(Path.Combine(src.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"HostRosterPinWriter\.Write\(|RosterPinCommands\."))
            .Select(f => Path.GetFileName(f)!).Order().ToArray();
        // A (same-PC app, the pipe's App-only handoff) is here; B (enrollment) will be added deliberately, never a relay or HA handler.
        Assert.Equal(["Program.cs", "RosterPinCommands.cs", "SamePcPinHandoff.cs"], callers);
        var program = File.ReadAllText(Path.Combine(src.FullName, "src", "AgentBridge.Host", "Program.cs"));
        Assert.Contains("if (command == \"roster-pin\")\n{\n    if (arguments.ElementAtOrDefault(1) != \"show\" && Refused(\"pin this host's device roster\", stateOnly: true)) { return 4; }",
            program.ReplaceLineEndings("\n"));
        Assert.Single(Regex.Matches(program, @"RosterPinCommands\.RunAsync\("));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
