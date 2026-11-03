using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Protocol;
using BenchmarkDotNet.Attributes;

namespace AgentBridge.Benchmarks;

// What the host does with a session each time it changes (SessionSource.Commit): the get form
// and the list form, each within the budget, and the JSON that decides whether anything a client
// sees changed. Oversized is the one SessionBudget.Fit has to trim, serializing it again at each
// step.
public class UpsertBuildBenchmarks
{
    Session _full = null!;

    [Params(Shape.Typical, Shape.P99, Shape.Oversized)]
    public Shape Size { get; set; }

    [GlobalSetup]
    public void Setup() => _full = Fixtures.Full(Size);

    [Benchmark]
    public string Commit()
    {
        var getForm = SessionBudget.Fit(_full);
        _ = SessionBudget.Fit(_full.WithoutHistory());
        return JsonSerializer.Serialize(getForm, ProtocolJson.Options);
    }
}

// One session's share of a discovery pass when its transcript has not changed, which is most
// passes for most sessions (one a second each): the tail finds nothing new, and the session is
// built from what was parsed and compared with the last one sent (SessionTracker.Update).
public class SessionTrackerBenchmarks
{
    string _dir = null!;
    SessionTracker _tracker = null!;
    DiscoveredSession _discovered = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("abbench-").FullName;
        var path = Path.Combine(_dir, "events.jsonl");
        using (var file = File.Create(path)) { Fixtures.WriteCopilotTranscript(file, turns: 200); }
        var options = new SessionSourceOptions { CopilotHome = _dir, ClaudeHome = _dir, CodexHome = _dir };
        _discovered = new DiscoveredSession("66666666-6666-4666-8666-666666666666", AgentKind.Copilot, path, StateDirectory: null, ProcessId: 39844);
        _tracker = new SessionTracker(_discovered, options, TimeProvider.System);
        _tracker.Update(_discovered);
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [Benchmark]
    public Session Unchanged() => _tracker.Update(_discovered);
}
