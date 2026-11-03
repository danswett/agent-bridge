using AgentBridge.App.Core;
using AgentBridge.Protocol;
using BenchmarkDotNet.Attributes;

namespace AgentBridge.Benchmarks;

// The sessions list arranged again, as the app does on every upsert: 300 sessions over three
// machines (the long list quality/README.md's perf-clients scrolls), every one already placed,
// ten pinned, in each of the three sorts.
public class SessionOrderBenchmarks
{
    static readonly string[] Machines = ["0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210", "00112233445566778899aabbccddeeff"];
    static readonly Dictionary<string, string> Names = new() { [Machines[0]] = "DEV-BOX-1", [Machines[1]] = "laptop", [Machines[2]] = "build-vm" };

    SessionOrder _order = null!;
    List<OrderEntry> _entries = null!;

    [Params(SessionSort.Yours, SessionSort.Recent, SessionSort.Machine)]
    public SessionSort Sort { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var template = Fixtures.Upsert(Shape.Typical);
        var t0 = template.UpdatedAt;
        _entries = [.. Enumerable.Range(0, 300).Select(i => new OrderEntry(
            Machines[i % Machines.Length],
            template with { Id = $"{i:x8}-6b35-49bd-b083-39572fbff648", UpdatedAt = t0.AddSeconds(-(i * 7919 % 300)) }))];
        // In memory only: nothing is written.
        _order = new SessionOrder(path: null) { Sort = Sort };
        _order.Arrange(_entries, Name, _ => true, _ => true);
        for (var i = 0; i < 10; i++) { _order.SetPinned(_entries[i * 30].Key, true); }
    }

    static string? Name(string machine) => Names.GetValueOrDefault(machine);

    [Benchmark]
    public Arrangement Arrange() => _order.Arrange(_entries, Name, _ => true, _ => true);
}

// A session card's reply rendered to the app's Markdown model, at a typical reply's length and at
// the 16 KiB a reply is cut to.
public class MarkdownBenchmarks
{
    string _text = null!;

    [Params(1500, Limits.Response)]
    public int Bytes { get; set; }

    [GlobalSetup]
    public void Setup() => _text = Fixtures.Prose(Bytes, seed: 2);

    [Benchmark]
    public IReadOnlyList<MdBlock> Parse() => MarkdownModel.Parse(_text);
}
