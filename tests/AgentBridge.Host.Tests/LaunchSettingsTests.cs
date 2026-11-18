using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Migration;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class LaunchSettingsTests : IAsyncLifetime
{
    const string Op = "host.launchFolders";
    readonly string folder = Directory.CreateTempSubdirectory("ab-launch-settings-").FullName;
    readonly string added = Path.Combine(Directory.GetCurrentDirectory(), "launch-folder-" + Guid.NewGuid().ToString("N"));
    readonly Callers callers = new();
    LocalServer server = null!;
    Func<IProtocolChannel, string, JsonElement, Task<object?>> handle = null!;
    string Config => Path.Combine(folder, HostSettings.FileName);
    string Kept => Path.Combine(folder, "unavailable");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(added);
        File.WriteAllText(Config, JsonSerializer.Serialize(new
        {
            version = 1, retained = 7,
            launch = new { enabled = false, remoteFolderTrust = false,
                workspaces = new[] { new { path = Kept, label = "Kept", isolate = true, fleetWorkers = true, retained = 9 } } },
        }));
        var control = new HostControl(new HostControlOptions
        {
            Profile = "test", Folder = folder, Version = "0.1.0", Running = new(true),
            Machine = new("test", "Test", "windows", "0.1.0"),
            SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => throw new NotSupportedException(),
            Stop = () => throw new NotSupportedException(), Restart = () => throw new NotSupportedException(),
        });
        handle = LocalApi.Handler(null!, control, callers);
        server = LocalServer.Start(LocalEndpoint.For(folder), handle);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(folder, true);
        Directory.Delete(added, true);
    }

    Task<LocalChannel> Connect() => LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(10));
    async Task<JsonElement> Call(IProtocolChannel channel, object data) =>
        JsonSerializer.SerializeToElement(await handle(channel, Op, JsonSerializer.SerializeToElement(data, ProtocolJson.Options)), ProtocolJson.Options);
    static string Baseline(JsonElement snapshot)
    {
        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        return snapshot.GetProperty("baseline").GetString()!;
    }

    [Fact]
    public async Task Snapshot_is_read_only_and_confirmed_add_remove_preserves_unavailable_entries_and_unrelated_metadata()
    {
        await using var channel = await Connect();
        var before = File.ReadAllText(Config);
        var snapshot = await Call(channel, new { });
        var baseline = Baseline(snapshot);
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.Equal(Kept, Assert.Single(snapshot.GetProperty("folders").EnumerateArray()).GetString());
        var saved = await Call(channel, new { baseline, folders = new[] { Kept, added }, confirmed = true });
        Assert.NotEqual(baseline, Baseline(saved));
        var root = JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal(7, root["retained"]!.GetValue<int>());
        Assert.False(root["launch"]!["enabled"]!.GetValue<bool>());
        Assert.False(root["launch"]!["remoteFolderTrust"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before)!["launch"]!["workspaces"]![0], root["launch"]!["workspaces"]![0]));
        var removed = await Call(channel, new { baseline = Baseline(saved), folders = new[] { Kept }, confirmed = true });
        Assert.Single(removed.GetProperty("folders").EnumerateArray());
    }

    [Fact]
    public async Task Unconfirmed_stale_and_racing_saves_leave_the_current_file_intact()
    {
        await using var channel = await Connect();
        var baseline = Baseline(await Call(channel, new { }));
        var before = File.ReadAllText(Config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(channel, new { baseline, folders = new[] { added } }));
        Assert.Equal(before, File.ReadAllText(Config));
        File.AppendAllText(Config, " ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(channel, new { baseline, folders = new[] { added }, confirmed = true }));
        baseline = Baseline(await Call(channel, new { }));
        var concurrent = before + "\n ";
        DurableFile.BeforeCommit.Value = path => File.WriteAllText(path, concurrent);
        try { await Assert.ThrowsAsync<DurableFile.ChangedMeanwhileException>(() => Call(channel, new { baseline, folders = new[] { Kept }, confirmed = true })); }
        finally { DurableFile.BeforeCommit.Value = null; }
        Assert.Equal(concurrent, File.ReadAllText(Config));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing")]
    [InlineData("system")]
    [InlineData("duplicate")]
    public async Task Invalid_new_folders_never_change_approval(string kind)
    {
        await using var channel = await Connect();
        var baseline = Baseline(await Call(channel, new { }));
        var requested = kind switch { "relative" => "relative", "missing" => added + "-missing", "system" => Path.GetPathRoot(added)!, _ => added };
        var before = File.ReadAllText(Config);
        var folders = kind == "duplicate" ? new[] { requested, requested } : new[] { requested };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Call(channel, new { baseline, folders, confirmed = true }));
        Assert.Equal(before, File.ReadAllText(Config));
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.HostCli)]
    [InlineData(CallerKind.Other)]
    public async Task Only_a_freshly_classified_local_app_may_read_or_save(CallerKind kind)
    {
        await using var channel = await Connect();
        Baseline(await Call(channel, new { }));
        callers.Kind = kind;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(channel, new { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(channel, new { baseline = "stale", folders = Array.Empty<string>(), confirmed = true }));
        Assert.True(callers.Fresh);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"launch":{"workspaces":false}}""")]
    public async Task Unreadable_configuration_is_never_replaced(string text)
    {
        await using var channel = await Connect();
        File.WriteAllText(Config, text);
        await Assert.ThrowsAnyAsync<Exception>(() => Call(channel, new { }));
        Assert.Equal(text, File.ReadAllText(Config));
    }

    [Fact]
    public async Task Remote_channels_cannot_read_or_save_even_if_classified_as_the_app()
    {
        foreach (var request in new object[] { new { }, new { baseline = "stale", folders = Array.Empty<string>(), confirmed = true } })
        { await Assert.ThrowsAsync<InvalidOperationException>(() => Call(new Remote(), request)); }
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

    sealed class Callers : ICallerGuard
    {
        public CallerKind Kind = CallerKind.App;
        public bool Fresh;
        public Caller Classify(IProtocolChannel channel, bool fresh = false) { Fresh = fresh; return new(Kind, "fixture"); }
    }
}
