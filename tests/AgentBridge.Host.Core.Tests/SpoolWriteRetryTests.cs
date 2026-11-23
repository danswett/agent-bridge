using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Hooks;
using AgentBridge.Protocol;
using Xunit;

// The intake writes a session's registration by writing a temp file and renaming it over the old one. On Windows that rename is refused
// (UnauthorizedAccessException, "access denied") for as long as another reader has the old file open without delete-sharing, so a
// registration write failed now and then (HookSpoolRetry in the diagnostics journal) and cost the event a whole extra pass. The commit
// is retried a few times with a short backoff. This is NOT an ACL or authentication change (#3847 is the spool's authentication).
public sealed class SpoolWriteRetryTests : IDisposable
{
    readonly Fixture _f = new();
    readonly List<string> _log = [];
    HookIntake? _intake;
    FileStream? _reader;
    Action<string>? _onLog;

    public void Dispose()
    {
        _reader?.Dispose();
        _intake?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _f.Dispose();
    }

    string Spool => SpoolContract.SpoolDirectory(_f.Runtime);
    string Target => Path.Combine(_f.Runtime, "agent-bridge-claude", "s1.json");

    HookIntake Intake()
    {
        _intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () => null,
            UseFileSystemWatcher = false,
            PruneInterval = TimeSpan.FromDays(1),
            SpaceProbe = HookIntake.ProbeSpace,
        }, _f.Processes, _f.Time, m => { lock (_log) { _log.Add(m); } _onLog?.Invoke(m); });
        return _intake;
    }

    void Spooled(string name, string cwd)
    {
        Directory.CreateDirectory(Spool);
        var ev = new { session_id = "s1", hook_event_name = "UserPromptSubmit", transcript_path = _f.ClaudeTranscript("s1"), cwd };
        var body = JsonSerializer.Serialize(new { v = 1, agent = "claude", hook = "register", ancestors = new[] { 10, 20, 30, 40 }, receivedAt = Fixture.T0.ToString("o"), @event = ev });
        File.WriteAllText(Path.Combine(Spool, name + ".json"), body);
    }

    string Cwd()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Target));
        return doc.RootElement.GetProperty("WorkingDirectory").GetString()!;
    }

    // The rename is refused for a reader without delete-sharing on Windows only; elsewhere a rename over an open file just works.
    static bool Applies => OperatingSystem.IsWindows();

    [Fact]
    public void A_registration_whose_old_file_a_reader_briefly_holds_open_is_written_on_a_retry_in_the_same_pass()
    {
        if (!Applies) { return; }
        _f.Processes.Run(AgentKind.Claude, 30);
        Spooled("0001", "/work/first");
        var intake = Intake();
        intake.Pass();
        Assert.Equal("/work/first", Cwd());

        // A reader opens the registration without FileShare.Delete and lets go the moment the first rename has been refused.
        _reader = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        _onLog = m => { if (m.Contains("needed a retry", StringComparison.Ordinal)) { _reader?.Dispose(); _reader = null; } };
        Spooled("0002", "/work/second");
        intake.Pass();

        Assert.Equal("/work/second", Cwd());
        Assert.Equal(1, intake.WriteRetries);
        Assert.DoesNotContain(_log, m => m.Contains("will retry", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, ".*.tmp"));
    }

    [Fact]
    public void A_real_failure_that_does_not_clear_still_fails_the_event_after_the_retries_and_leaves_nothing_behind()
    {
        if (!Applies) { return; }
        _f.Processes.Run(AgentKind.Claude, 30);
        Spooled("0001", "/work/first");
        var intake = Intake();
        intake.Pass();

        _reader = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Spooled("0002", "/work/second");
        intake.Pass();

        _reader.Dispose(); _reader = null;
        Assert.Equal("/work/first", Cwd());
        Assert.Equal(2, intake.WriteRetries);
        Assert.Contains(_log, m => m.Contains("failed, will retry", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, ".*.tmp"));
        // The event is kept for the intake's own second try, which now succeeds because nothing holds the file.
        intake.Pass();
        Assert.Equal("/work/second", Cwd());
    }
}
