using System.Diagnostics;
using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class SelfTestResultTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-17T20:00:00Z");

    [Fact]
    public async Task An_unfinished_stage_keeps_its_name_elapsed_time_and_last_real_progress()
    {
        var time = new FakeTimeProvider(Start);
        var writer = new SelfTestResult(time);
        var result = new Dictionary<string, object?>();
        var file = Path.Combine(Path.GetTempPath(), $"selftest-{Guid.NewGuid():N}.json");
        try
        {
            await Write(writer, file, result, "live resize", "01 setup");
            time.Advance(TimeSpan.FromSeconds(2));
            await Write(writer, file, result, "live resize", "02 light sweep");
            time.Advance(TimeSpan.FromSeconds(5));
            await Write(writer, file, result, "live resize", null);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal("live resize", json.RootElement.GetProperty("unfinished").GetString());
            var stage = Assert.Single(json.RootElement.GetProperty("stages").EnumerateArray());
            Assert.Equal("live resize", stage.GetProperty("name").GetString());
            Assert.Equal(7, stage.GetProperty("elapsedSeconds").GetDouble());
            Assert.Equal(Start, stage.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(Start.AddSeconds(2), stage.GetProperty("lastProgressAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, stage.GetProperty("endedAt").ValueKind);
            Assert.Equal("02 light sweep", stage.GetProperty("step").GetString());
            var steps = stage.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Equal(new[] { 2d, 5d }, steps.Select(s => s.GetProperty("elapsedSeconds").GetDouble()));
            Assert.Equal(Start.AddSeconds(2), steps[0].GetProperty("endedAt").GetDateTimeOffset());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_finished_stage_keeps_its_end_and_elapsed_time_when_the_result_is_written_again()
    {
        var time = new FakeTimeProvider(Start);
        var writer = new SelfTestResult(time);
        var result = new Dictionary<string, object?>();
        var file = Path.Combine(Path.GetTempPath(), $"selftest-{Guid.NewGuid():N}.json");
        try
        {
            await Write(writer, file, result, "high contrast", null);
            time.Advance(TimeSpan.FromSeconds(3));
            await Write(writer, file, result, "live resize", "16 report");
            time.Advance(TimeSpan.FromSeconds(7));
            await Write(writer, file, result, null, null);
            time.Advance(TimeSpan.FromSeconds(20));
            await Write(writer, file, result, null, null);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var stages = json.RootElement.GetProperty("stages").EnumerateArray().ToArray();
            Assert.Equal(3, stages[0].GetProperty("elapsedSeconds").GetDouble());
            var stage = Assert.Single(stages, s => s.GetProperty("name").GetString() == "live resize");
            Assert.Equal(7, stage.GetProperty("elapsedSeconds").GetDouble());
            Assert.Equal(Start.AddSeconds(10), stage.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal(Start.AddSeconds(10), stage.GetProperty("lastProgressAt").GetDateTimeOffset());
        }
        finally { File.Delete(file); }
    }

    static Task Write(SelfTestResult writer, string file, Dictionary<string, object?> result, string? stage, string? step) =>
        writer.WriteAsync(file, result, ["passed"], [], stage, step);

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(5, false, false, "still progressing at 15 reader list")]
    [InlineData(90, false, false, "stalled at 15 reader list")]
    [InlineData(5, true, false, "completed; awaiting process exit")]
    [InlineData(5, true, true, "missed process exit")]
    [InlineData(5, true, true, "late completion", 110)]
    public async Task The_wrapper_classifies_progress_without_treating_a_timeout_as_proof_of_a_hang(
        int silence, bool completed, bool exited, string expected, int completedSecond = 90)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Path.Exists(Path.Combine(root.FullName, ".git"))) { root = root.Parent; }
        Assert.NotNull(root);
        var helper = Path.Combine(root.FullName, "build", "app-self-test-progress.ps1");
        Assert.True(File.Exists(helper), "The wrapper has no stage timing classifier.");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["SELFTEST_HELPER"] = helper;
        start.Environment["SELFTEST_RESULT"] = JsonSerializer.Serialize(new
        {
            unfinished = completed ? null : "live resize",
            completedAt = completed ? Start.AddSeconds(completedSecond) : (DateTimeOffset?)null,
            stages = new[] { new { name = "live resize", startedAt = Start, endedAt = completed ? Start.AddSeconds(completedSecond) : (DateTimeOffset?)null,
                lastProgressAt = Start.AddSeconds(120 - silence), elapsedSeconds = 120 - silence, step = "15 reader list" } },
        });
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            ". $env:SELFTEST_HELPER; Get-SelfTestStageEvidence -Result ($env:SELFTEST_RESULT | ConvertFrom-Json) " +
            $"-ObservedAt '{Start.AddSeconds(120):O}' -Deadline '{Start.AddSeconds(100):O}' " +
            $"-WaitFinished $false -ProcessExited ${exited} | ConvertTo-Json -Depth 6" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        using var evidence = JsonDocument.Parse(await output);
        Assert.StartsWith(expected, evidence.RootElement.GetProperty("classification").GetString());
        Assert.Equal("live resize", Assert.Single(evidence.RootElement.GetProperty("stages").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(evidence.RootElement.GetProperty("waitFinished").GetBoolean());
        Assert.Equal(exited, evidence.RootElement.GetProperty("processExited").GetBoolean());
    }
}
