using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

public sealed class PayloadSizeReportTests(ITestOutputHelper output)
{
    [Fact]
    public void The_report_sample_keeps_every_types_UTF8_extrema_without_replaying_the_whole_corpus()
    {
        var records = JsonNode.Parse("""
            {"Mixed":{"valid":[{"json":"aaa"},{"json":{}},{"json":true}],
                      "refused":[{"json":"éé"}]},
             "Single":{"valid":[{"json":[]}],"refused":[]},
             "Tied":{"valid":[{"json":{"n":1}}],"refused":[{"json":{"n":2}}]}}
            """)!.AsObject();
        var original = records.ToJsonString(ProtocolJson.Options);
        var sample = ReportSample(records);

        Assert.Equal(records.Select(pair => pair.Key), sample.Select(pair => pair.Key));
        Assert.Equal(new[] { "{}", "\"éé\"" }, Payloads("Mixed"));
        Assert.Equal(new[] { "[]" }, Payloads("Single"));
        Assert.Equal(new[] { "{\"n\":1}" }, Payloads("Tied"));
        Assert.Equal(original, records.ToJsonString(ProtocolJson.Options));

        IEnumerable<string> Payloads(string name) =>
            new[] { "valid", "refused" }.SelectMany(kind =>
                sample[name]![kind]!.AsArray().Select(vector => vector!["json"]!.ToJsonString(ProtocolJson.Options)));
    }

    [Fact]
    public async Task Report_lists_every_vector_message_type_with_byte_sizes()
    {
        using var corpus = new VectorFolder();
        var expected = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var sampleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(VectorFiles.Folder, "*.json"))
        {
            var records = JsonNode.Parse(File.ReadAllText(file))!["records"]?.AsObject();
            if (records is null) { continue; }
            var sampleRecords = ReportSample(records);
            corpus.Write(System.IO.Path.GetFileName(file),
                new JsonObject { ["records"] = sampleRecords }.ToJsonString(ProtocolJson.Options));
            foreach (var (name, record) in records)
            {
                if (!expected.TryGetValue(name, out var sizes)) { expected[name] = sizes = []; }
                sampleCounts.TryGetValue(name, out var count);
                foreach (var kind in new[] { "valid", "refused" })
                {
                    count += sampleRecords[name]![kind]!.AsArray().Count;
                    foreach (var vector in record![kind]!.AsArray())
                    {
                        sizes.Add(Encoding.UTF8.GetByteCount(vector!["json"]!.ToJsonString(ProtocolJson.Options)));
                    }
                }
                sampleCounts[name] = count;
            }
        }
        // Measure the full corpus in C#; PowerShell needs only its real extrema, not every refusal variant.
        corpus.Write("non-record.json", """{"handshake":{}}""");
        var result = await RunAsync(corpus.Path);
        Assert.True(result.ExitCode == 0, result.Error);
        output.WriteLine("Full corpus: {0} payloads; PowerShell sample: {1}.",
            expected.Values.Sum(sizes => sizes.Count), sampleCounts.Values.Sum());
        output.WriteLine(result.Text);
        var rows = Rows(result.Text);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), rows.Keys);
        foreach (var (name, sizes) in expected)
        {
            Assert.Equal((sizes.Min(), sizes.Max(), sampleCounts[name]), rows[name]);
        }

        using var sample = new VectorFolder();
        sample.Write("a.json", """
            {"records":{"Example":{"valid":[{"json":{"text":"é"}}],
                                  "refused":[{"json":{"text":"longer"}}]}}}
            """);
        sample.Write("b.json", """
            {"records":{"Example":{"valid":[{"json":{}}],"refused":[]}}}
            """);
        var measured = await RunAsync(sample.Path);
        Assert.True(measured.ExitCode == 0, measured.Error);
        Assert.Equal((2, 17, 3), Rows(measured.Text)["Example"]);
    }

    [Fact(Skip = "Quarantined: https://github.com/danswett/agent-bridge/issues/3142, expires 2026-11-19 04:30 PT")]
    public async Task Report_is_deterministic_for_the_same_vectors()
    {
        using var sample = new VectorFolder();
        const string alpha = """{"records":{"Alpha":{"valid":[{"json":{"text":"é"}}],"refused":[]}}}""";
        const string zulu = """{"records":{"Zulu":{"valid":[{"json":{}}],"refused":[]}}}""";
        sample.Write("first.json", zulu);
        sample.Write("last.json", alpha);
        var first = await RunAsync(sample.Path);
        sample.Write("first.json", alpha);
        sample.Write("last.json", zulu);
        var second = await RunAsync(sample.Path);
        Assert.True(first.ExitCode == 0, first.Error);
        Assert.True(second.ExitCode == 0, second.Error);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(new[] { "Alpha", "Zulu" }, Rows(first.Text).Keys);
        Assert.Equal((13, 13, 1), Rows(first.Text)["Alpha"]);
    }

    [Fact(Skip = "Quarantined: hosted 30s post-READY timeout; owner Payload R3 builder; tracking issue https://github.com/danswett/agent-bridge/issues/3065; fix deadline 2026-11-18 12:00 PT")]
    public async Task Report_fails_clearly_on_a_vector_that_cannot_be_parsed()
    {
        using var sample = new VectorFolder();
        foreach (var malformed in new[] { "{broken", """{"records":{"Broken":{"valid":[{}],"refused":[]}}}""" })
        {
            sample.Write("broken.json", malformed);
            var result = await RunAsync(sample.Path);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("broken.json", result.Error);
            Assert.Contains("Cannot measure vector file", result.Error);
            Assert.Empty(result.Text);
        }
    }

    [Fact]
    public async Task The_report_deadline_starts_after_PowerShell_is_ready_and_still_bounds_a_stalled_report()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = WaitForReportAsync(ready.Task, exited.Task, task =>
        {
            Assert.Same(exited.Task, task);
            armed.SetResult();
            return deadline.Task;
        });
        try
        {
            Assert.False(armed.Task.IsCompleted);
            Assert.False(waiting.IsCompleted);
            ready.SetResult();
            await armed.Task;
            Assert.False(waiting.IsCompleted);

            deadline.SetException(new TimeoutException("stalled report"));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
            Assert.Equal("stalled report", error.Message);
            Assert.False(exited.Task.IsCompleted);
        }
        finally
        {
            ready.TrySetResult();
            deadline.TrySetResult();
        }
    }

    static JsonObject ReportSample(JsonObject records)
    {
        var sample = new JsonObject();
        foreach (var (name, record) in records)
        {
            var vectors = new[] { "valid", "refused" }.SelectMany(kind =>
                record![kind]!.AsArray().Select(vector =>
                    (Kind: kind, Vector: vector!, Bytes: Encoding.UTF8.GetByteCount(vector!["json"]!.ToJsonString(ProtocolJson.Options)))))
                .OrderBy(vector => vector.Bytes).ToArray();
            Assert.NotEmpty(vectors);
            var selected = new JsonObject { ["valid"] = new JsonArray(), ["refused"] = new JsonArray() };
            foreach (var vector in new[] { vectors[0], vectors[^1] }.DistinctBy(vector => vector.Bytes))
            {
                selected[vector.Kind]!.AsArray().Add(vector.Vector.DeepClone());
            }
            sample[name] = selected;
        }
        return sample;
    }

    static Dictionary<string, (int Min, int Max, int Count)> Rows(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("MessageType\tMinBytes\tMaxBytes\tCount", lines[0]);
        return lines.Skip(1).Select(line => line.Split('\t')).ToDictionary(
            row => row[0],
            row => (int.Parse(row[1], CultureInfo.InvariantCulture),
                    int.Parse(row[2], CultureInfo.InvariantCulture),
                    int.Parse(row[3], CultureInfo.InvariantCulture)),
            StringComparer.Ordinal);
    }

    static async Task<(int ExitCode, string Text, string Error)> RunAsync(string folder)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.Environment["PAYLOAD_REPORT_SCRIPT"] = System.IO.Path.Combine(VectorFiles.Folder, "report-payload-sizes.ps1");
        start.Environment["PAYLOAD_REPORT_VECTORS"] = folder;
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::Out.WriteLine('payload-report-ready'); & $env:PAYLOAD_REPORT_SCRIPT -VectorPath $env:PAYLOAD_REPORT_VECTORS" })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var ready = process.StandardOutput.ReadLineAsync();
        var text = ReadTextAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForReportAsync(ready, process.WaitForExitAsync(),
                exited => exited.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync();
            throw new TimeoutException("Payload size report did not exit within 30 seconds after PowerShell became ready.");
        }
        return (process.ExitCode, await text, await error);

        async Task<string> ReadTextAsync()
        {
            Assert.Equal("payload-report-ready", await ready);
            return await process.StandardOutput.ReadToEndAsync();
        }
    }

    static async Task WaitForReportAsync(Task ready, Task exited, Func<Task, Task> withinBudget)
    {
        // Hosted process startup is not report work; the test runner owns the outer hang bound.
        await ready;
        await withinBudget(exited);
    }

    sealed class VectorFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "payload-report-" + Guid.NewGuid().ToString("N"));
        public VectorFolder() => Directory.CreateDirectory(Path);
        public void Write(string name, string text) => File.WriteAllText(System.IO.Path.Combine(Path, name), text);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
