using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Transport;
using Xunit;

public sealed class SessionRecoveryTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-recovery-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(folder, "profile-reading.json");
    readonly List<string> log = [];

    [Fact]
    public void The_latest_selection_and_scroll_anchor_survive_without_transcript_text()
    {
        var saved = new SessionReading("local\nsecond", new SessionPosition(420, false, new ScrollAnchor(3, -12), "turn-1", -8, "reply-1"));
        var store = new SessionRecovery(FilePath, log.Add);
        store.Remember(new("local\nfirst", null));
        store.Remember(saved);
        Assert.Equal(saved, new SessionRecovery(FilePath, log.Add).Current);
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(["Key", "Position"], json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Empty(log);
        Assert.Null(new SessionRecovery(Path.Combine(folder, "other-reading.json"), log.Add).Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gone")]
    [InlineData("second")]
    public async Task Startup_restores_the_saved_session_or_the_first_after_the_whole_snapshot(string? saved)
    {
        var store = new SessionRecovery(FilePath, log.Add);
        if (saved is not null) { store.Remember(new("local\n" + saved, new(80, false))); }
        store = new SessionRecovery(FilePath, log.Add);
        await using var host = Connection();
        var main = CreateMain(host, store);
        host.Sessions.BeginSnapshot();
        host.Sessions.Upsert(Make.Session("first"));
        Assert.Null(main.Selected);
        host.Sessions.Upsert(Make.Session("second"));
        host.Sessions.EndSnapshot();
        Assert.Equal(saved == "second" ? "second" : main.Sessions[0].Id, main.Selected?.Id);
        if (saved == "second") { Assert.Equal(80, store.Current?.Position?.Offset); }
    }

    [Fact]
    public async Task Selecting_a_different_session_saves_immediately_and_discards_the_old_anchor()
    {
        await using var host = Connection();
        var main = CreateMain(host, new SessionRecovery(FilePath, log.Add));
        host.Sessions.Upsert(Make.Session("first"));
        host.Sessions.Upsert(Make.Session("second"));
        main.Selected = main.Sessions.Single(c => c.Id == "first");
        main.Recovery!.Remember(new("local\nfirst", new(100, false)));
        main.Selected = main.Sessions.Single(c => c.Id == "second");
        Assert.Equal(new SessionReading("local\nsecond", null), new SessionRecovery(FilePath, log.Add).Current);
        host.Sessions.BeginSnapshot();
        host.Sessions.EndSnapshot();
        Assert.Null(main.Selected);
    }

    [Fact]
    public async Task A_delayed_remote_snapshot_restores_the_right_session_not_a_local_id_collision()
    {
        var store = new SessionRecovery(FilePath, log.Add);
        var saved = new SessionReading("peer-id\nsame", new(90, false));
        store.Remember(saved);
        await using var local = Connection();
        var main = CreateMain(local, store);
        Snapshot(local, "same");
        await using var remote = Connection();
        main.Add(new Machine("peer-id", remote, Context(remote)));
        Snapshot(remote, "same");
        Assert.Same(main.AllCards.Single(c => main.KeyOf(c) == saved.Key), main.Selected);
        Assert.Equal(saved, store.Current);
    }

    [Fact]
    public async Task A_persons_choice_before_the_snapshot_finishes_is_not_overridden()
    {
        var store = new SessionRecovery(FilePath, log.Add);
        store.Remember(new("local\nsecond", null));
        await using var host = Connection();
        var main = CreateMain(host, store);
        host.Sessions.BeginSnapshot();
        host.Sessions.Upsert(Make.Session("first"));
        main.Selected = main.Sessions.Single();
        host.Sessions.Upsert(Make.Session("second"));
        host.Sessions.EndSnapshot();
        Assert.Equal("first", main.Selected?.Id);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Key\":null}")]
    public void Corrupt_or_unwritable_state_is_reported_without_preventing_app_use(string corrupt)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, corrupt);
        Assert.Null(new SessionRecovery(FilePath, log.Add).Current);
        var store = new SessionRecovery(Path.Combine(FilePath, "blocked.json"), log.Add);
        var saved = new SessionReading("local\nfirst", null);
        store.Remember(saved);
        Assert.Equal(saved, store.Current);
        Assert.Equal(2, log.Count);
    }

    static HostConnection Connection() => new(_ => Task.FromException<IClientChannel>(new InvalidOperationException("No connection expected")), new LockedDispatcher());
    static CardContext Context(HostConnection host) => new() { Deliveries = new Deliveries(host, new LockedDispatcher()), Folds = new FoldStore(null), Capabilities = () => [], Host = host };
    static MainViewModel CreateMain(HostConnection host, SessionRecovery store) => new(new Machine(Machine.LocalKey, host, Context(host)), new UsageViewModel(), recovery: store);
    static void Snapshot(HostConnection host, string id) { host.Sessions.BeginSnapshot(); host.Sessions.Upsert(Make.Session(id)); host.Sessions.EndSnapshot(); }
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
}
