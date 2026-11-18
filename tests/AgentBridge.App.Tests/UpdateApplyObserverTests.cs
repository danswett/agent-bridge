using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Xunit;

public sealed class UpdateApplyObserverTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-observer-" + Guid.NewGuid().ToString("N"));
    string ResultPath => Path.Combine(folder, "coordinator-result.json");
    string Updater => Path.Combine(folder, "install", "Update.exe");

    public UpdateApplyObserverTests() => Directory.CreateDirectory(folder);

    static UpdateApplyResult Result(UpdateApplyEnd end) => new(end, "exit-failed", "apply-completion",
        "0.1.2", 7, null, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(12));

    [Theory]
    [InlineData(UpdateApplyEnd.Failed, 22)]
    [InlineData(UpdateApplyEnd.Unknown, null)]
    public void Unsuccessful_observations_report_step_timing_and_only_the_updater_identity(UpdateApplyEnd end, int? pid)
    {
        new UpdateApplyObserver(ResultPath).Report(Result(end), pid, Updater);

        using var document = JsonDocument.Parse(File.ReadAllBytes(ResultPath));
        var payload = document.RootElement;
        Assert.Equal(Result(end), payload.Deserialize<UpdateApplyResult>());
        Assert.Equal("updater", payload.GetProperty("ApplyProcessRole").GetString());
        Assert.Equal(Updater, payload.GetProperty("ApplyPath").GetString());
        var message = payload.GetProperty("Diagnostic").GetString()!;
        Assert.Contains(end.ToString(), message);
        Assert.Contains("step=apply-completion", message);
        Assert.Contains($"updater PID={pid?.ToString() ?? "unknown"}", message);
        Assert.Contains($"updater path={Updater}", message);
        Assert.Contains("elapsed=00:00:12", message);
        Assert.DoesNotContain("holder", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_oversized_observation_is_refused_without_creating_a_partial_record()
    {
        var observer = new UpdateApplyObserver(ResultPath);
        Assert.Throws<InvalidDataException>(() =>
            observer.Report(Result(UpdateApplyEnd.Unknown) with { Step = new string('x', 64 * 1024) }, 22, Updater));
        Assert.False(File.Exists(ResultPath));
    }

    [Fact]
    public void An_existing_record_is_never_replaced_or_appended_to()
    {
        var observer = new UpdateApplyObserver(ResultPath);
        observer.Report(Result(UpdateApplyEnd.Completed), 22, Updater);
        var original = File.ReadAllBytes(ResultPath);
        Assert.Throws<IOException>(() => observer.Report(Result(UpdateApplyEnd.Unknown), null, Updater));
        Assert.Equal(original, File.ReadAllBytes(ResultPath));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
