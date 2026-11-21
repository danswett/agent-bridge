using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host;
using AgentBridge.Local;
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
    // The fixture genesis is v1; tests stand in K1's v3 predicate unless they check the refusal.
    string Write(PersonGenesis? source, RelayRoster? relay, string account = RosterFixture.Account, bool v3 = true) =>
        HostRosterPinWriter.Write(PinPath, source, relay, account, () => w.Now, _ => v3);

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
    public async Task A_v1_genesis_is_refused_plainly_and_writes_no_file()
    {
        var (exit, output, error) = await Cli("set", Typed.ToUpperInvariant());
        Assert.Equal((1, ""), (exit, output));
        Assert.Contains("\"state\":\"genesis-not-v3\"", error);
        Assert.Contains("pin refused: genesis predates capSign; the account needs a v3 genesis", error);
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public void A_genesis_the_capSign_predicate_rejects_is_refused_and_writes_no_file()
    {
        Assert.Equal("genesis-not-v3", Write(Source(), Relay(), v3: false));
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public async Task A_capSign_genesis_writes_a_four_field_pin_that_show_prints_as_one_untrusted_line()
    {
        Assert.Equal("pinned", Write(Source(Typed.ToUpperInvariant()), Relay()));
        using var pin = JsonDocument.Parse(File.ReadAllBytes(PinPath));
        Assert.Equal(["account", "generation", "genesis", "heads"], pin.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("verified", new HostRosterReader(PinPath, () => w.Now).Update(Relay(), RosterFixture.Account).Outcome);
        var (exit, output, _) = await Cli("show", null);
        Assert.Equal((0, $"UNTRUSTED genesis fingerprint: {Typed}{Environment.NewLine}"), (exit, output));
    }

    [Fact]
    public void A_pin_folder_blocked_by_a_file_is_a_closed_pin_store_refusal()
    {
        File.WriteAllText(Path.GetDirectoryName(PinPath)!, "");
        Assert.Equal("pin-store", Write(Source(), Relay()));
    }

    [Fact]
    public void The_command_line_guard_knows_this_host_by_its_own_path_on_every_platform()
    {
        var guard = RosterPinCommands.Guard(() => [], () => "/opt/agent-bridge/agent-bridge-host");
        Assert.Equal(CallerKind.HostCli, guard.Classify([new PeerProcess(1, null, "/opt/agent-bridge/agent-bridge-host")]).Kind);
    }

    [Fact]
    public void A_mismatched_fingerprint_writes_nothing()
    {
        var wrong = Typed[..^1] + (Typed[^1] == '0' ? '1' : '0');
        Assert.Equal("fingerprint-mismatch", Write(Source(wrong), Relay()));
        Assert.False(Directory.Exists(Path.GetDirectoryName(PinPath)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(PinPath)!).Any());
    }

    [Fact]
    public async Task An_existing_pin_is_never_overwritten_or_even_fetched_against()
    {
        Assert.Equal("pinned", Write(Source(), Relay()));
        var before = File.ReadAllBytes(PinPath);
        Assert.Equal("pin-exists", Write(Source(), Relay()));
        var (exit, _, error) = await Cli("set", Typed);
        Assert.Equal(1, exit);
        Assert.Contains("pin-exists", error);
        Assert.Equal(0, fetches);
        Assert.Equal(before, File.ReadAllBytes(PinPath));
    }

    [Fact]
    public async Task A_relay_genesis_for_another_account_refuses()
    {
        Assert.Equal("other-account", Write(Source(), Relay(), "someone-else@example"));
        Assert.Contains("account-mismatch", (await Cli("set", Typed, relayAccount: "someone-else@example")).Err);
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public void Every_ambiguity_refuses_with_a_closed_code_and_writes_nothing()
    {
        Assert.Null(PersonGenesis.FromFingerprint(Typed[..9]));
        Assert.Null(PersonGenesis.FromFingerprint(Typed.Replace(' ', '-')));
        Assert.Null(PersonGenesis.FromFingerprint(Typed + "0"));
        Assert.Equal("no-source", Write(null, Relay()));
        Assert.Equal("no-roster", Write(Source(), null));
        Assert.Equal("generation-mismatch", Write(Source(), Relay("00")));
        Assert.Equal("no-genesis", Write(Source(), new("off", w.GenerationId, [w.Entry("B").Canonical()])));
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
        // A (same-PC app) and B (enrollment) will be added here deliberately, never a relay, HA or pipe handler.
        Assert.Equal(["Program.cs", "RosterPinCommands.cs"], callers);
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
