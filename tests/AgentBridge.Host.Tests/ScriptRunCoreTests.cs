using AgentBridge.Host;
using AgentBridge.ScriptRun;
using Xunit;

// #3305 slice S1b-1 (docs/design/script-credentials-run.md sections 2, 3, 5 and 7): the CORE of the admin-locked launcher, behind small ports. The order is fixed
// (parse, admit, read the token, start the child, tell the host), the token exists only in the child's environment block and is zeroed on every path, a refusal is
// one closed code on stderr and exit 125, and a child that itself exits 125 is mapped to 1.
public sealed class ScriptRunCoreTests
{
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    const string Script = @"C:\Locked\Cycle.ps1";
    const string LauncherExe = @"C:\Program Files\Slipgates\script-run.exe";
    static readonly string[] Good = ["dispatcher", "--", Pwsh, "-NoProfile", "-NonInteractive", "-File", Script];

    // ---- the command rules ----

    [Fact]
    public void A_command_with_a_name_a_separator_and_an_absolute_program_is_understood()
    {
        var parsed = CommandLine.Parse(Good)!;
        Assert.Equal(("dispatcher", Pwsh), (parsed.Name, parsed.Program));
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", Script], parsed.Arguments);
        Assert.Equal(["dispatcher"], [CommandLine.Parse(["dispatcher", "--", @"C:\Tools\cycle.exe"])!.Name]);
    }

    [Theory]
    [InlineData("dispatcher")]
    [InlineData("dispatcher", Pwsh)]
    [InlineData("--", Pwsh)]
    [InlineData("..", "--", Pwsh)]
    [InlineData("a/b", "--", @"C:\Tools\cycle.exe")]
    [InlineData("dispatcher", "--", "pwsh.exe", "-NoProfile", "-NonInteractive", "-File", Script)]
    [InlineData("dispatcher", "--", @"C:\%ProgramFiles%\pwsh.exe", "-NoProfile", "-NonInteractive", "-File", Script)]
    [InlineData("dispatcher", "--", Pwsh, "-NoProfile", "-NonInteractive", "-Command", "& C:\\Locked\\Cycle.ps1")]
    [InlineData("dispatcher", "--", Pwsh, "-NoProfile", "-NonInteractive", "-c", "x")]
    [InlineData("dispatcher", "--", Pwsh, "-NonInteractive", "-File", Script)]
    [InlineData("dispatcher", "--", Pwsh, "-NoProfile", "-File", Script)]
    [InlineData("dispatcher", "--", Pwsh, "-NoProfile", "-NonInteractive", "-File", "Cycle.ps1")]
    [InlineData("dispatcher", "--", Pwsh, "-NoProfile", "-NonInteractive", "-Unknown", "-File", Script)]
    [InlineData("dispatcher", "--", @"C:\Tools\cycle.exe", "--flag")]
    [InlineData("dispatcher", "--", @"C:\Windows\System32\cmd.exe", "/c", @"C:\Locked\Cycle.cmd")]
    public void Anything_the_task_binding_could_not_pin_is_a_bad_command(params string[] args) => Assert.Null(CommandLine.Parse(args));

    // The host pins the task's action with ScriptTaskXml; the launcher must refuse exactly what the pin refuses, or a command could run that nothing pinned.
    [Fact]
    public void The_launcher_and_the_task_pin_agree_on_every_command_shape()
    {
        const string Launcher = @"C:\Program Files\Slipgates\script-run.exe";
        string Xml(string arguments) => "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal id=\"A\"><LogonType>InteractiveToken</LogonType></Principal></Principals>"
            + $"<Actions><Exec><Command>{Launcher}</Command><Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments></Exec></Actions></Task>";
        var shapes = new[]
        {
            "dispatcher -- \"" + Pwsh + "\" -NoProfile -NonInteractive -File \"" + Script + "\"",
            @"dispatcher -- C:\Tools\cycle.exe",
            @"fleet.d-1 -- C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe /NoProfile /NonInteractive /File " + Script,
            "dispatcher", "dispatcher -- pwsh.exe -NoProfile -NonInteractive -File C:\\Locked\\Cycle.ps1", ".. -- " + Pwsh,
            "dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -Command x", "dispatcher -- " + Pwsh + " -NonInteractive -File " + Script,
            @"dispatcher -- C:\Tools\cycle.exe --flag", @"dispatcher -- C:\Windows\System32\cmd.exe /c C:\Locked\Cycle.cmd",
            "dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Script,
        };
        foreach (var shape in shapes)
        {
            var tokens = CommandLine.Tokens(shape);
            Assert.True((tokens is not null && CommandLine.Parse(tokens) is not null) == (ScriptTaskXml.ActionFiles(Xml(shape)) is not null), $"the launcher and the pin disagree on: {shape}");
        }
    }

    // ---- the run ----

    sealed class Host(bool unavailable = false, bool refuse = false) : IHostPort
    {
        public IReadOnlyList<string> Pinned { get; init; } = [LauncherExe, Pwsh, Script];
        public List<string> Calls { get; } = [];
        public Guid Id { get; } = Guid.NewGuid();
        public int? FinishedExit { get; private set; }
        public bool FinishedFails { get; init; }
        public Task<Admission> AdmitAsync(string name, int version, CancellationToken ct)
        {
            Calls.Add($"admit {name} v{version}");
            if (unavailable) { throw new HostUnavailableException(); }
            if (refuse) { throw new HostRefusedException(); }
            return Task.FromResult(new Admission(Id, "abc.key", Pinned));
        }
        public Task FinishedAsync(Guid admission, int exit, CancellationToken ct)
        {
            Calls.Add($"finished {exit}");
            FinishedExit = exit;
            return FinishedFails ? throw new IOException("pipe gone") : Task.CompletedTask;
        }
    }

    sealed class Tokens(List<string> calls, bool missing = false) : ITokenSource
    {
        public byte[]? LastToken { get; private set; }
        public byte[]? Read(string file) { calls.Add($"read {file}"); return LastToken = missing ? null : "abat1_SECRETSECRETSECRET"u8.ToArray(); }
    }

    sealed class Starter(List<string> calls, int exit = 0, bool fails = false) : IChildStarter
    {
        public char[]? Block { get; private set; }
        public string? Program { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string? BlockText { get; private set; }
        public Task<int> RunAsync(string program, IReadOnlyList<string> arguments, char[] environment, CancellationToken ct)
        {
            calls.Add("start");
            (Program, Arguments, Block, BlockText) = (program, arguments, environment, new string(environment));
            return fails ? throw new IOException("cannot start") : Task.FromResult(exit);
        }
    }

    static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows", ["PATH"] = @"C:\Windows\System32", ["AGENT_BRIDGE_TOKEN"] = "stale", ["agent_bridge_pipe"] = "evil", ["ComSpec"] = @"C:\Windows\System32\cmd.exe",
    };

    static async Task<(int Exit, string Err, Host Host, Tokens Tokens, Starter Starter)> Run(string[] args, Host? host = null, bool tokenMissing = false, int childExit = 0, bool startFails = false)
    {
        host ??= new Host();
        var tokens = new Tokens(host.Calls, tokenMissing);
        var starter = new Starter(host.Calls, childExit, startFails);
        var err = new StringWriter();
        var exit = await new Launcher(host, tokens, starter, err).RunAsync(args, Env, CancellationToken.None);
        return (exit, err.ToString(), host, tokens, starter);
    }

    [Fact]
    public async Task The_order_is_admit_then_token_then_child_then_finished_and_the_childs_exit_passes_through()
    {
        var run = await Run(Good, childExit: 3);
        Assert.Equal(3, run.Exit);
        Assert.Equal(["admit dispatcher v1", "read abc.key", "start", "finished 3"], run.Host.Calls);
        Assert.Equal(Pwsh, run.Starter.Program);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", Script], run.Starter.Arguments);
        Assert.Equal("", run.Err);
    }

    [Fact]
    public async Task The_child_environment_is_the_tasks_minus_every_agent_bridge_variable_plus_the_token_sorted_and_zeroed_after()
    {
        var run = await Run(Good);
        var lines = run.Starter.BlockText!.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["AGENT_BRIDGE_TOKEN=abat1_SECRETSECRETSECRET", "ComSpec=C:\\Windows\\System32\\cmd.exe", "PATH=C:\\Windows\\System32", "SystemRoot=C:\\Windows"], lines);
        Assert.DoesNotContain("evil", run.Starter.BlockText);
        Assert.DoesNotContain("stale", run.Starter.BlockText);
        Assert.All(run.Starter.Block!, c => Assert.Equal('\0', c)); // zeroed once the child has started
        Assert.All(run.Tokens.LastToken!, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(true, false, "refused")]
    [InlineData(false, true, "host-unavailable")]
    public async Task A_refused_or_unreachable_host_is_one_closed_code_exit_125_and_the_token_is_never_read(bool refuse, bool unavailable, string code)
    {
        var run = await Run(Good, new Host(unavailable: unavailable, refuse: refuse));
        Assert.Equal((125, code + Environment.NewLine), (run.Exit, run.Err));
        Assert.Equal(["admit dispatcher v1"], run.Host.Calls);
    }

    [Fact]
    public async Task A_bad_command_never_reaches_the_host()
    {
        var run = await Run(["dispatcher", "--", "pwsh.exe"]);
        Assert.Equal((125, "bad-command" + Environment.NewLine), (run.Exit, run.Err));
        Assert.Empty(run.Host.Calls);
    }

    [Fact]
    public async Task A_token_file_that_cannot_be_read_or_a_child_that_cannot_start_is_a_closed_refusal_and_the_token_is_zeroed()
    {
        var missing = await Run(Good, tokenMissing: true);
        Assert.Equal((125, "refused" + Environment.NewLine), (missing.Exit, missing.Err));
        Assert.DoesNotContain("start", missing.Host.Calls);
        var fails = await Run(Good, startFails: true);
        Assert.Equal((125, "bad-command" + Environment.NewLine), (fails.Exit, fails.Err));
        Assert.All(fails.Starter.Block!, c => Assert.Equal('\0', c));
        Assert.All(fails.Tokens.LastToken!, b => Assert.Equal(0, b));
        Assert.DoesNotContain("SECRET", fails.Err);
    }

// The launcher compares what the OS handed it with what the host pinned: a differently-read argv runs nothing (#4524 P2-1).
    [Fact]
    public async Task An_argv_that_differs_from_the_pinned_files_is_refused_before_the_token_is_read()
    {
        var otherScript = await Run(Good, new Host { Pinned = [LauncherExe, Pwsh, @"C:\Locked\Other.ps1"] });
        Assert.Equal((125, "refused" + Environment.NewLine), (otherScript.Exit, otherScript.Err));
        var otherProgram = await Run(Good, new Host { Pinned = [LauncherExe, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Script] });
        Assert.Equal(125, otherProgram.Exit);
        var missingScript = await Run(Good, new Host { Pinned = [LauncherExe, Pwsh] });
        Assert.Equal(125, missingScript.Exit);
        foreach (var run in new[] { otherScript, otherProgram, missingScript }) { Assert.Equal(["admit dispatcher v1"], run.Host.Calls); }
    }

    [Fact]
    public async Task The_crafted_argv_the_read_described_runs_nothing_because_the_script_it_would_run_is_not_the_pinned_one()
    {
        // What Windows hands pwsh for the crafted action: the first -File names the unpinned script, the pinned one is a later, ignored switch.
        string[] argv = ["dispatcher", "--", Pwsh, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "x" + (char)34, "-File", @"C:\Evil\evil.ps1", ((char)34).ToString(), "-File", Script];
        var run = await Run(argv);
        Assert.Equal((125, "refused" + Environment.NewLine), (run.Exit, run.Err));
        Assert.DoesNotContain("start", run.Host.Calls);
    }

    [Fact]
    public async Task A_program_with_no_script_is_pinned_as_launcher_and_program_only()
    {
        var run = await Run(["dispatcher", "--", @"C:\Tools\cycle.exe"], new Host { Pinned = [LauncherExe, @"C:\Tools\cycle.exe"] });
        Assert.Equal(0, run.Exit);
        Assert.Contains("start", run.Host.Calls);
        var extra = await Run(["dispatcher", "--", @"C:\Tools\cycle.exe"], new Host { Pinned = [LauncherExe, @"C:\Tools\cycle.exe", Script] });
        Assert.Equal(125, extra.Exit);
    }

    [Fact]
    public async Task A_child_that_exits_125_is_mapped_to_1_and_a_lost_finished_message_changes_nothing()
    {
        var run = await Run(Good, new Host { FinishedFails = true }, childExit: 125);
        Assert.Equal(1, run.Exit);
        Assert.Equal(125, run.Host.FinishedExit); // the host hears what the child really returned
        Assert.Equal("", run.Err);
    }
}
