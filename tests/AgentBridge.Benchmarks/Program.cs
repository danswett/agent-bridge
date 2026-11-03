using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace AgentBridge.Benchmarks;

// bench-micro (quality/perf.md):
//   dotnet run -c Release --project tests/AgentBridge.Benchmarks -- --filter '*' [--summary <file.json>]
// Every BenchmarkDotNet option works. --summary also writes the small JSON a baseline is kept as.
public static class Program
{
    public static int Main(string[] args)
    {
        string? summaryPath = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--summary" && i + 1 < args.Length) { summaryPath = args[++i]; }
            else { rest.Add(args[i]); }
        }

        var config = DefaultConfig.Instance.AddDiagnoser(MemoryDiagnoser.Default);
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run([.. rest], config).ToList();
        var reports = summaries.SelectMany(s => s.Reports).ToList();
        if (summaryPath is not null && reports.Count > 0) { WriteSummary(summaryPath, reports); }
        // Not a gate on the numbers (README §3.2): a benchmark that throws, or a config BenchmarkDotNet
        // refuses, is a broken benchmark, and only that fails the run.
        return summaries.Any(s => s.HasCriticalValidationErrors) || reports.Any(r => !r.Success) ? 1 : 0;
    }

    static void WriteSummary(string path, IReadOnlyList<BenchmarkDotNet.Reports.BenchmarkReport> reports)
    {
        var benchmarks = new JsonArray();
        foreach (var report in reports.OrderBy(r => r.BenchmarkCase.Descriptor.Type.Name, StringComparer.Ordinal)
                     .ThenBy(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name, StringComparer.Ordinal)
                     .ThenBy(r => r.BenchmarkCase.Parameters.PrintInfo, StringComparer.Ordinal))
        {
            var stats = report.ResultStatistics;
            benchmarks.Add(new JsonObject
            {
                ["name"] = $"{report.BenchmarkCase.Descriptor.Type.Name}.{report.BenchmarkCase.Descriptor.WorkloadMethod.Name}",
                ["parameters"] = report.BenchmarkCase.Parameters.PrintInfo,
                ["meanNs"] = Round(stats?.Mean),
                ["medianNs"] = Round(stats?.Median),
                ["p95Ns"] = Round(stats?.Percentiles.P95),
                ["stdDevNs"] = Round(stats?.StandardDeviation),
                ["allocatedBytes"] = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase),
            });
        }

        long transcriptBytes;
        using (var counter = new CountingStream()) { Fixtures.WriteCopilotTranscript(counter, Fixtures.LargeTranscriptTurns); transcriptBytes = counter.Length; }
        var doc = new JsonObject
        {
            ["suite"] = "bench-micro",
            ["schema"] = 1,
            ["platform"] = Platform(),
            ["measuredAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["environment"] = new JsonObject
            {
                ["runner"] = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "github-actions" : "local",
                ["os"] = RuntimeInformation.OSDescription,
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["processors"] = Environment.ProcessorCount,
            },
            // What the sizes were, so a change to the fixtures is not read as a change in speed.
            ["fixtures"] = new JsonObject
            {
                ["upsertBytes"] = new JsonObject
                {
                    ["typical"] = ProtocolJson.ByteCount(Fixtures.Upsert(Shape.Typical)),
                    ["p99"] = ProtocolJson.ByteCount(Fixtures.Upsert(Shape.P99)),
                    ["oversized"] = ProtocolJson.ByteCount(Fixtures.Upsert(Shape.Oversized)),
                },
                ["oversizedBeforeFitBytes"] = ProtocolJson.ByteCount(Fixtures.Full(Shape.Oversized).WithoutHistory()),
                ["transcriptBytes"] = transcriptBytes,
            },
            ["benchmarks"] = benchmarks,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    static double? Round(double? ns) => ns is { } v ? Math.Round(v, 1) : null;

    // As quality/catalog.yml names platforms.
    static string Platform() =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux") + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    sealed class CountingStream : Stream
    {
        long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void WriteByte(byte value) => _length++;
    }
}
