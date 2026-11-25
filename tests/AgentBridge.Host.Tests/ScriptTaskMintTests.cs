using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

// #3305 S2: the app's task-bound mint press reaches the host as scripts.add with a task, the extra files and the list the person was shown. The host pins through the real
// ScriptTaskBinding.Pin and refuses unless what it reads now is exactly what the person saw.
public sealed class ScriptTaskMintTests : IDisposable
{
    const string Launcher = @"C:\Program Files\Slipgates\script-run\script-run.exe";
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    const string Script = @"C:\Program Files\Slipgates\fleet\Cycle.ps1";
    const string Extra = @"C:\Program Files\Slipgates\fleet\lib.ps1";
    const string TaskPath = @"\Slipgates\dispatcher";

    readonly Fixture fixture = new();
    readonly Tasks tasks = new();
    readonly AgentCredentials credentials;

    public ScriptTaskMintTests() => credentials = new(fixture.Root, "machine1", tasks: tasks);
    public void Dispose() => fixture.Dispose();

    sealed class Tasks : IScriptTaskReader
    {
        public List<string> Unlocked { get; } = [];
        public bool LoggedOn { get; set; } = true;
        public string? Definition(string task) => new string('a', 64);
        public IReadOnlyList<string>? ActionFiles(string task) => [Launcher, Pwsh, Script];
        public IReadOnlyList<ScriptTaskEngine> RunningEngines(string task) => [];
        public bool TaskLocked(string task) => true;
        public bool FileLocked(string path) => !Unlocked.Contains(path);
        public string? FileSha256(string path) => new string('b', 64);
        public bool LoggedOnOnly(string task) => LoggedOn;
    }

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(CallerKind kind = CallerKind.App) =>
        LocalApi.Handler(new SessionService(null!, new MachineInfo("machine1", "Test", "windows", "0.1.0")),
            null!, new FixedCallerGuard(new Caller(kind, "fixture")), credentials: credentials, audit: new AgentAuditLog(fixture.Root));

    static readonly string[] Shown = [Launcher, Pwsh, Script, Extra];

    // The app's press: the host's confirmation for exactly this action and name, then the press carrying it.
    async Task Press(Func<IProtocolChannel, string, JsonElement, Task<object?>> handler, string name, Dictionary<string, object?> body)
    {
        var channel = new Channel();
        var asked = JsonSerializer.SerializeToElement(await handler(channel, "scripts.confirm", JsonSerializer.SerializeToElement(new { op = "scripts.add", name })), ProtocolJson.Options);
        body["name"] = name;
        body["confirm"] = asked.GetProperty("confirm").GetString();
        await handler(channel, "scripts.add", JsonSerializer.SerializeToElement(body));
    }

    [Fact]
    public void A_press_with_a_task_pins_the_launcher_the_interpreter_the_script_and_each_extra_file()
    {
        if (!OperatingSystem.IsWindows()) { return; } // the pin checks drive-letter paths, which is Windows' rule
        var minted = credentials.MintScript("dispatcher", task: TaskPath, files: [Extra], shown: Shown);
        Assert.Equal(Shown, minted.Credential.ScheduledTask!.Files.Select(f => f.Path));
        Assert.Equal(TaskPath, minted.Credential.ScheduledTask.Task);
    }


    [Fact]
    public void The_mint_is_refused_and_nothing_is_stored_when_the_files_the_host_reads_differ_from_the_list_the_person_was_shown()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        foreach (var shown in new[] { new[] { Launcher, Pwsh, Script }, [Launcher, Pwsh, @"C:\Evil\Cycle.ps1", Extra], [Launcher, Pwsh, Script, Extra, @"C:\More\x.ps1"], []})
        {
            var refused = Assert.Throws<InvalidOperationException>(() => credentials.MintScript("dispatcher", task: TaskPath, files: [Extra], shown: shown));
            Assert.Contains("differ from what was shown", refused.Message);
        }
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public void The_mint_is_refused_when_any_pinned_file_can_be_changed_without_elevation_or_the_task_is_not_logged_on_only()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        foreach (var path in Shown)
        {
            tasks.Unlocked.Clear();
            tasks.Unlocked.Add(path);
            Assert.Contains("can be changed without elevation", Assert.Throws<InvalidOperationException>(() => credentials.MintScript("dispatcher", task: TaskPath, files: [Extra], shown: Shown)).Message);
        }
        tasks.Unlocked.Clear();
        tasks.LoggedOn = false;
        Assert.Contains("stored password", Assert.Throws<InvalidOperationException>(() => credentials.MintScript("dispatcher", task: TaskPath, files: [Extra], shown: Shown)).Message);
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public async Task The_pipe_press_carries_task_files_and_shown_and_a_malformed_or_unshown_task_press_is_an_argument_error()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var handler = Handler();
        await Assert.ThrowsAsync<ArgumentException>(() => Press(handler, "dispatcher", new() { ["task"] = TaskPath, ["files"] = "x", ["shown"] = Shown }));
        await Assert.ThrowsAsync<ArgumentException>(() => Press(handler, "dispatcher", new() { ["task"] = 5, ["files"] = new[] { Extra }, ["shown"] = Shown }));
        // A task press that shows the person nothing is never minted.
        await Assert.ThrowsAsync<ArgumentException>(() => Press(handler, "dispatcher", new() { ["task"] = TaskPath, ["files"] = new[] { Extra } }));
        await Assert.ThrowsAsync<ArgumentException>(() => Press(handler, "dispatcher", new() { ["task"] = TaskPath, ["files"] = new[] { Extra }, ["shown"] = Array.Empty<string>() }));
        Assert.Equal(0, credentials.Count);
        await Press(handler, "dispatcher", new() { ["task"] = TaskPath, ["files"] = new[] { Extra }, ["shown"] = Shown });
        Assert.Equal(1, credentials.Count);
    }

    [Fact]
    public async Task Only_the_apps_press_may_bind_a_task_the_host_command_line_is_refused()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Assert.ThrowsAsync<ArgumentException>(() => Handler(CallerKind.HostCli)(new Channel(), "scripts.add",
            JsonSerializer.SerializeToElement(new { name = "dispatcher", task = TaskPath, files = new[] { Extra }, shown = Shown })));
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public async Task A_press_without_a_task_mints_an_unbound_credential_exactly_as_before()
    {
        await Press(Handler(), "pump-wake", []);
        Assert.Equal(1, credentials.Count);
        Assert.Null(credentials.MintScript("pump-wake").Credential.ScheduledTask);
    }

    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
