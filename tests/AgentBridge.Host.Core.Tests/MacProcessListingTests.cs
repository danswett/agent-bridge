using System.Buffers.Binary;
using System.Text;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

public sealed class MacProcessListingTests
{
    static readonly DateTimeOffset Started = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1234560);

    sealed class Native : IMacProcessListingNative
    {
        public int[] Pids { get; set; } = [1, 2, 3, 4, 5];
        public Func<int, string?> ReadPath { get; set; } = pid => pid switch
        { 1 => "/bin/CODEX", 2 => "/bin/codex.exe", 3 => "/bin/node", 4 => "/bin/bun", _ => "/bin/unrelated" };
        public Func<int, MacProcessDetails?> ReadDetails { get; set; } = pid => new(pid, "fallback", Started, 3);
        public int ListCalls { get; private set; }
        public List<int> DetailPids { get; } = [];
        public Func<int[]?, int>? ListResult { get; set; }
        public int List(int[]? buffer)
        {
            ListCalls++;
            if (ListResult is not null) { return ListResult(buffer); }
            if (buffer is not null) { Pids.CopyTo(buffer, 0); }
            return Pids.Length;
        }
        public string? Path(int pid) => ReadPath(pid);
        public MacProcessDetails? Details(int pid) { DetailPids.Add(pid); return ReadDetails(pid); }
    }

    [Fact]
    public void All_candidate_names_share_one_PID_snapshot_without_inspecting_unrelated_tasks()
    {
        var native = new Native();
        var found = Assert.IsAssignableFrom<IReadOnlyList<MacListedProcess>>(
            MacProcessListing.Capture(["codex", "codex.exe", "node", "bun"], native));
        Assert.Equal(new[] { "codex", "codex.exe", "node", "bun" }, found.Select(p => p.Candidate));
        Assert.Equal(2, native.ListCalls); // one size query and one filled snapshot
        Assert.Equal(new[] { 1, 2, 3, 4 }, native.DetailPids);
        Assert.All(found, p => Assert.Equal(Started, p.StartedAt));
    }

    [Fact]
    public void An_unreadable_path_uses_the_BSD_name_and_a_known_name_retains_unknown_generation()
    {
        var native = new Native { Pids = [1, 2], ReadPath = pid => pid == 1 ? null : "/bin/node",
            ReadDetails = pid => pid == 1 ? new(pid, "codex", Started, 3) : null };
        var found = MacProcessListing.Capture(["codex", "node"], native)!;
        Assert.Equal(2, found.Count);
        Assert.Equal("codex", found[0].Candidate);
        Assert.Null(found[1].StartedAt);
        var (observation, _) = SystemProcessInspector.Classify(AgentKind.Codex, "codex",
            found.Select(p => new SystemProcessInspector.ListedProcess(p.Candidate, p.Id, p.StartedAt, null, p.Parent)),
            listed: true, windows: false, _ => false);
        Assert.False(observation.Known);
        Assert.Equal(2, Assert.Single(observation.Unidentified).Id);
        Assert.Equal(1, Assert.Single(observation.Processes).Id);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    [InlineData("full")]
    public void Failed_or_truncated_PID_snapshots_are_not_reported_as_complete_absence(string kind)
    {
        var native = new Native { Pids = kind == "negative" ? [-1] : [1, 1] };
        if (kind == "failure") { native.ListResult = _ => 0; }
        if (kind == "full") { native.ListResult = buffer => buffer?.Length ?? 1; }
        Assert.Null(MacProcessListing.Capture(["codex"], native));
        Assert.InRange(native.ListCalls, 1, 5);
    }

    [Fact]
    public void A_growing_PID_list_is_retried_before_any_process_is_classified()
    {
        var native = new Native();
        var reads = 0;
        native.ListResult = buffer =>
        {
            if (buffer is null) { return 1; }
            if (++reads == 1) { return buffer.Length; }
            buffer[0] = 1;
            return 1;
        };
        Assert.Equal(1, Assert.Single(MacProcessListing.Capture(["codex"], native)!).Id);
        Assert.Equal(3, native.ListCalls);
        Assert.Equal(new[] { 1 }, native.DetailPids);
    }

    [Fact]
    public void BSD_identity_preserves_microseconds_and_rejects_wrong_PID_or_short_native_data()
    {
        var bytes = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 456);
        Encoding.UTF8.GetBytes("codex").CopyTo(bytes, 48);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), 1_700_000_000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(128), 123456);
        var info = Assert.IsType<MacProcessDetails>(MacProcessListingNative.Decode(123, bytes));
        Assert.Equal(Started, info.StartedAt);
        Assert.Equal(456, info.Parent);
        Assert.Equal("codex", info.Name);
        Assert.Null(MacProcessListingNative.Decode(124, bytes));
        Assert.Null(MacProcessListingNative.Decode(123, bytes.AsSpan(0, 135)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(128), 1_000_000);
        Assert.Null(MacProcessListingNative.Decode(123, bytes)!.StartedAt);
    }
}
