using System.Diagnostics;
using System.Reflection;
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
        var writer = Writer(time);
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
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_finished_stage_keeps_its_end_and_elapsed_time_when_the_result_is_written_again()
    {
        var time = new FakeTimeProvider(Start);
        var writer = Writer(time);
        var result = new Dictionary<string, object?>();
        var file = Path.Combine(Path.GetTempPath(), $"selftest-{Guid.NewGuid():N}.json");
        try
        {
            await Write(writer, file, result, "live resize", "16 report");
            time.Advance(TimeSpan.FromSeconds(7));
            await Write(writer, file, result, null, null);
            time.Advance(TimeSpan.FromSeconds(20));
            await Write(writer, file, result, null, null);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var stage = Assert.Single(json.RootElement.GetProperty("stages").EnumerateArray());
            Assert.Equal(7, stage.GetProperty("elapsedSeconds").GetDouble());
            Assert.Equal(Start.AddSeconds(7), stage.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal(Start.AddSeconds(7), stage.GetProperty("lastProgressAt").GetDateTimeOffset());
        }
        finally { File.Delete(file); }
    }

    // Reflection lets the new writer's absence be an assertion RED, not a compilation failure.
    static object Writer(TimeProvider time)
    {
        var type = typeof(FrameTimes).Assembly.GetType("AgentBridge.App.Core.SelfTestResult");
        Assert.NotNull(type);
        return Activator.CreateInstance(type, time)!;
    }

    static Task Write(object writer, string file, Dictionary<string, object?> result, string? stage, string? step) =>
        (Task)writer.GetType().GetMethod("WriteAsync", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(writer, [file, result, new List<string> { "passed" }, new List<string>(), stage, step])!;

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
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".git"))) { root = root.Parent; }
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
    }
}
