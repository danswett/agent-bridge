using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// host.workspace.add: the person in the app approves a folder with no terminal, through the command line's one check; anyone else is refused.
public sealed class WorkspaceAddOpTests : IAsyncLifetime
{
    const string Op = HostOps.WorkspaceAdd;
    readonly string folder = Path.Combine(Path.GetTempPath(), "abwo-" + Guid.NewGuid().ToString("N")[..8]);
    // Under the test's own folder rather than temp, which the system-folder check refuses.
    readonly string dirs = Path.Combine(AppContext.BaseDirectory, "abwo-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> logged = [];
    readonly List<IReadOnlyList<WorkspaceConfig>> applied = [];
    readonly Callers callers = new();
    LocalServer server = null!;
    Func<IProtocolChannel, string, JsonElement, Task<object?>> handler = null!;
    string ConfigPath => Path.Combine(folder, HostSettings.FileName);
    string Dir(string name) => Directory.CreateDirectory(Path.Combine(dirs, name)).FullName;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(ConfigPath, """{ "version": 1, "retained": 7 }""");
        var control = new HostControl(new HostControlOptions
        {
            Profile = "test", Folder = folder, Version = "0.1.0",
            Machine = new MachineInfo("test", "Test", "windows", "0.1.0"),
            Running = new HostSettingsView(true),
            SignIn = (_, _, _) => throw new NotSupportedException(),
            SignedIn = (_, _) => throw new NotSupportedException(),
            Stop = () => throw new NotSupportedException(),
            Restart = () => throw new NotSupportedException(),
            ApplyWorkspaces = applied.Add,
        }, logged.Add);
        handler = LocalApi.Handler(null!, control, callers, logged.Add);
        server = LocalServer.Start(LocalEndpoint.For(folder), handler);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(folder, recursive: true);
        if (Directory.Exists(dirs)) { Directory.Delete(dirs, recursive: true); }
    }

    Task<LocalChannel> Connect() => LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task The_app_approves_a_folder_and_it_is_applied_to_the_running_host_with_no_restart()
    {
        var app = Dir("My Tools");
        await using var ch = await Connect();

        var result = await ch.CallAsync(Op, new { path = app, label = "Tools" });

        Assert.Equal(("added", "Tools", app, false), (result.GetProperty("result").GetString(), result.GetProperty("label").GetString(),
            result.GetProperty("path").GetString(), result.GetProperty("restartRequired").GetBoolean()));
        Assert.Equal([("Tools", app)], Assert.Single(applied).Select(w => (w.Label, w.Path)));
        var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal(7, saved["retained"]!.GetValue<int>());
        Assert.Equal((app, "Tools"), (saved["launch"]!["workspaces"]![0]!["path"]!.GetValue<string>(), saved["launch"]!["workspaces"]![0]!["label"]!.GetValue<string>()));
        Assert.Single(logged, line => line.Contains(Op, StringComparison.Ordinal) && line.Contains("approved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("already-approved")]
    public async Task A_refused_folder_gets_its_closed_code_and_writes_nothing_and_is_audited(string code)
    {
        var app = Dir("app");
        await using var ch = await Connect();
        await ch.CallAsync(Op, new { path = app });
        var before = File.ReadAllText(ConfigPath);
        logged.Clear();
        var path = code == "missing" ? Path.Combine(dirs, "nothing-here") : app;

        var result = await ch.CallAsync(Op, new { path });

        Assert.Equal(("refused", code), (result.GetProperty("result").GetString(), result.GetProperty("code").GetString()));
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Single(logged, line => line.Contains(Op, StringComparison.Ordinal) && line.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_network_share_is_refused_with_its_closed_code()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await Connect();

        var result = await ch.CallAsync(Op, new { path = @"\\server\share\work" });

        Assert.Equal(("refused", "not-local"), (result.GetProperty("result").GetString(), result.GetProperty("code").GetString()));
    }

    [Theory]
    [InlineData("""{"path":"$p","isolate":true}""")]
    [InlineData("""{"path":7}""")]
    [InlineData("""{"path":""}""")]
    [InlineData("""{"path":"$p","path":"$p"}""")]
    [InlineData("""{"path":"$p","label":5}""")]
    [InlineData("""{"path":"$p","label":"bad\u0007label"}""")]
    [InlineData("""{"path":"$p","label":"a‮b"}""")]
    [InlineData("""{"path":"$p","label":"a​b"}""")]
    public async Task A_request_of_any_other_shape_is_invalid_and_writes_nothing(string json)
    {
        var before = File.ReadAllText(ConfigPath);
        await using var ch = await Connect();

        var result = await ch.CallAsync(Op, JsonSerializer.Deserialize<JsonElement>(json.Replace("$p", JsonEncodedText.Encode(Dir("ok")).ToString(), StringComparison.Ordinal)));

        Assert.Equal(("refused", "invalid-request"), (result.GetProperty("result").GetString(), result.GetProperty("code").GetString()));
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task A_configuration_that_does_not_load_clean_is_refused_and_nothing_is_written_or_applied()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1, "launch": { "workspaces": [ { "path": "x", "isolate": 1 } ] } }""");
        await using var ch = await Connect();

        var before = File.ReadAllText(ConfigPath);

        var result = await ch.CallAsync(Op, new { path = Dir("a") });

        Assert.Equal(("refused", "unwritable"), (result.GetProperty("result").GetString(), result.GetProperty("code").GetString()));
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Empty(applied);
    }

    [Fact]
    public void If_applying_fails_after_the_folder_was_saved_it_is_still_reported_approved_and_says_restart()
    {
        var app = Dir("a");

        var result = WorkspaceAddOp.Handle(folder, JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { path = app })), logged.Add, _ => throw new InvalidOperationException("boom"));

        var answer = JsonSerializer.SerializeToElement(result);
        Assert.Equal(("added", true), (answer.GetProperty("result").GetString(), answer.GetProperty("restartRequired").GetBoolean()));
        Assert.Equal([app], HostSettings.Load(folder).Settings.Launch.Workspaces.Select(w => w.Path));
        Assert.Single(logged, line => line.Contains("applies after a restart", StringComparison.Ordinal));
    }

    [Fact]
    public void Where_the_host_cannot_apply_a_workspace_the_answer_says_restart()
    {
        var result = WorkspaceAddOp.Handle(folder, JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { path = Dir("a") })), logged.Add);

        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("restartRequired").GetBoolean());
    }

    [Fact]
    public async Task The_audit_line_quotes_what_the_person_typed_so_it_cannot_fake_another_field()
    {
        await using var ch = await Connect();

        await ch.CallAsync(Op, new { path = Dir("a"), label = "Tools' by 'someone" });

        var quoted = "\"Tools" + '\\' + "u0027 by " + '\\' + "u0027someone\"";
        Assert.Single(logged, line => line.Contains(quoted, StringComparison.Ordinal));
    }

    [Fact]
    public void Text_that_is_not_valid_Unicode_is_an_invalid_request_not_an_exception()
    {
        var result = WorkspaceAddOp.Handle(folder, JsonSerializer.Deserialize<JsonElement>("""{"path":"\ud800"}"""), logged.Add);

        Assert.Equal("invalid-request", JsonSerializer.SerializeToElement(result).GetProperty("code").GetString());
        Assert.Single(logged);
    }

    [Fact]
    public async Task A_config_file_that_is_not_json_is_left_alone_and_reported_with_a_closed_code()
    {
        File.WriteAllText(ConfigPath, "{broken");
        await using var ch = await Connect();

        var result = await ch.CallAsync(Op, new { path = Dir("a") });

        Assert.Equal("config-unreadable", result.GetProperty("code").GetString());
        Assert.Equal("{broken", File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.Other)]
    [InlineData(CallerKind.HostCli)]
    public async Task Only_a_person_in_the_app_may_approve_a_folder_even_on_an_existing_app_connection(CallerKind kind)
    {
        await using var ch = await Connect();
        await ch.CallAsync(Op, new { path = Dir("first") });
        var before = File.ReadAllText(ConfigPath);
        logged.Clear();
        callers.Kind = kind;

        var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Op, new { path = Dir("second") }));

        Assert.Contains("person-only", error.Message);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains(logged, line => line.Contains("refused", StringComparison.Ordinal) && line.Contains(Op, StringComparison.Ordinal));
        Assert.True(callers.Fresh);
    }

    [Fact]
    public async Task A_remote_channel_is_refused_even_when_classified_as_the_app()
    {
        var before = File.ReadAllText(ConfigPath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler(new Remote(), Op, JsonSerializer.SerializeToElement(new { path = Dir("a") })));

        Assert.Contains("person-only", error.Message);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    sealed class Callers : ICallerGuard
    {
        public CallerKind Kind { get; set; } = CallerKind.App;
        public bool Fresh { get; private set; }
        public Caller Classify(IProtocolChannel channel, bool fresh = false) { Fresh = fresh; return new(Kind, "fixture"); }
    }

    sealed class Remote : IProtocolChannel
    {
        public string PeerId => "remote";
        public string PeerName => "Remote";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
