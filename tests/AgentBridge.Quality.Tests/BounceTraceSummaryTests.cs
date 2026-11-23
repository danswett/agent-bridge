using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBridge.Quality.Tests;

// main's win-arm64 installer job failed four "bottom bounce" checks that all read one sticky latch, invalidSurface, and
// nothing in the job's output said WHICH frame or term tripped it: the trace the app writes
// (app.history-bottom-bounce.json) was not part of what the job keeps. build/app-self-test-trace.ps1 reads it on a
// failure and names the first invalid frame and the validity terms it failed, bounded; no assertion changes.
public sealed class BounceTraceSummaryTests
{
    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }
        return root;
    }

    // One recorded frame, as SelfTest.HistoryBottomBounce writes it.
    static JsonObject Frame(int ms, bool valid = true, int drawn = 9, int visibleHistory = 2, int visibleResponse = 0, double extent = 63403, double extentFloor = 60000, double viewport = 700, double offset = 100) => new()
    {
        ["ms"] = ms, ["phase"] = "control-wheel:input-free-tail", ["kind"] = "render", ["offset"] = offset, ["extent"] = extent, ["viewport"] = viewport,
        ["width"] = 900, ["rows"] = 364, ["storedRows"] = 364, ["requiredRows"] = 364, ["extentFloor"] = extentFloor, ["growthFloor"] = 61000,
        ["arrived"] = false, ["surfaceValid"] = valid, ["invalidSurface"] = !valid,
        ["drawnHistory"] = drawn, ["visibleHistory"] = visibleHistory, ["visibleResponse"] = visibleResponse,
    };

    static JsonNode? Summary(params JsonObject[] frames)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bounce-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, new JsonArray(frames.Select(f => (JsonNode?)f).ToArray()).ToJsonString());
        try
        {
            var script = Path.Combine(Root(), "build", "app-self-test-trace.ps1").Replace("'", "''");
            var command = $". '{script}'; $s = Get-BounceTraceSummary -Path '{file.Replace("'", "''")}'; if ($null -eq $s) {{ 'null' }} else {{ $s | ConvertTo-Json -Depth 6 -Compress }}";
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-Command", command]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output == "null" ? null : JsonNode.Parse(output);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void The_first_invalid_frame_and_the_terms_it_failed_are_named_even_far_past_the_head_kept()
    {
        var frames = Enumerable.Range(1, 80).Select(i => Frame(i * 16)).Append(Frame(1300, valid: false, drawn: 0, visibleHistory: 0, visibleResponse: 0)).Append(Frame(1316, valid: false)).ToArray();

        var s = Summary(frames)!;

        Assert.Equal(1300, (int)s["firstInvalid"]!["ms"]!);
        var why = s["why"]!.AsArray().Select(w => (string)w!).ToList();
        Assert.Contains(why, w => w.Contains("drawn history 0"));
        Assert.Contains(why, w => w.Contains("nothing in view"));
        Assert.Equal(82, (int)s["frames"]!);
    }

    [Fact]
    public void An_extent_below_its_floor_is_named_as_the_term_not_as_an_empty_surface()
    {
        var s = Summary(Frame(16), Frame(32, valid: false, extent: 59000, extentFloor: 60000))!;

        var why = s["why"]!.AsArray().Select(w => (string)w!).ToList();
        Assert.Contains(why, w => w.Contains("extent 59000") && w.Contains("floor 60000"));
        Assert.DoesNotContain(why, w => w.Contains("drawn history"));
    }

    [Fact]
    public void The_summary_is_bounded_whatever_the_trace_holds()
    {
        var frames = Enumerable.Range(1, 5000).Select(i => Frame(i)).Append(Frame(5001, valid: false, drawn: 0)).ToArray();

        var s = Summary(frames)!;

        Assert.True(s["head"]!.AsArray().Count <= 12);
        Assert.True(s["tail"]!.AsArray().Count <= 6);
        Assert.Equal(5001, (int)s["frames"]!);
        Assert.Equal(5001, (int)s["firstInvalid"]!["ms"]!);
    }

    [Fact]
    public void A_trace_with_no_invalid_frame_says_so_and_a_missing_or_unreadable_file_is_nothing()
    {
        var s = Summary(Frame(16), Frame(32))!;
        Assert.Null(s["firstInvalid"]);

        var missing = Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.json");
        var script = Path.Combine(Root(), "build", "app-self-test-trace.ps1").Replace("'", "''");
        var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-Command", $". '{script}'; $s = Get-BounceTraceSummary -Path '{missing}'; if ($null -eq $s) {{ 'null' }} else {{ 'something' }}"]) { RedirectStandardOutput = true };
        using var p = Process.Start(start)!;
        Assert.Equal("null", p.StandardOutput.ReadToEnd().Trim());
        p.WaitForExit();
    }
}
