using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.App.Tests;

public class FeedbackDiagnosticsTests
{
    [Fact]
    public void DraftIsBoundedAndIsolatedFromLaterEvents()
    {
        var diagnostics = new FeedbackDiagnostics();
        for (var i = 0; i < 101; i++)
        {
            diagnostics.Record(i == 0 ? FeedbackEvent.Refused : FeedbackEvent.Connected);
            diagnostics.Record(FeedbackLog.HostConnectionError);
        }
        var draft = diagnostics.CreateDraft("1.2.3", "4.5.6");
        diagnostics.Record(FeedbackEvent.SessionsRemove);

        using var preview = JsonDocument.Parse(draft.Preview("A note"));
        var root = preview.RootElement;
        Assert.Equal("slipgates-feedback.v1", root.GetProperty("schema").GetString());
        Assert.Equal("A note", root.GetProperty("note").GetString());
        Assert.Equal(100, root.GetProperty("events").GetArrayLength());
        Assert.All(root.GetProperty("events").EnumerateArray(), e => Assert.Equal("connection.connected", e.GetProperty("name").GetString()));
        Assert.Equal(100, root.GetProperty("logs").GetArrayLength());
        Assert.All(root.GetProperty("logs").EnumerateArray(), e => Assert.Equal("transport-error", e.GetProperty("code").GetString()));
        Assert.Equal("4.5.6", root.GetProperty("versions").GetProperty("hosts")[0].GetString());
        using var second = JsonDocument.Parse(draft.Preview("Different note"));
        Assert.Equal(root.GetProperty("bundleId").GetString(), second.RootElement.GetProperty("bundleId").GetString());
        Assert.Equal(root.GetProperty("createdAt").GetString(), second.RootElement.GetProperty("createdAt").GetString());
    }

    [Fact]
    public void RejectsUntrustedVersionsAndInvalidInput()
    {
        var diagnostics = new FeedbackDiagnostics();
        var draft = diagnostics.CreateDraft("token=secret", "Bearer secret");
        var serialized = draft.Preview("");
        Assert.DoesNotContain("secret", serialized);
        using var preview = JsonDocument.Parse(serialized);
        Assert.Equal("Unknown", preview.RootElement.GetProperty("versions").GetProperty("app").GetString());
        Assert.Empty(preview.RootElement.GetProperty("versions").GetProperty("hosts").EnumerateArray());
        Assert.False(preview.RootElement.TryGetProperty("note", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => draft.Preview(new string('x', 1001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => diagnostics.Record((FeedbackEvent)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => diagnostics.Record((FeedbackLog)999));
    }

    [Fact]
    public async Task ConnectionFailuresRecordOnlyAnAllowListedCode()
    {
        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string secret = "token=example-secret";
        await using var connection = new HostConnection(
            _ => Task.FromException<IClientChannel>(new IOException(secret)),
            new LockedDispatcher(),
            _ => logged.TrySetResult()) { RetryDelay = TimeSpan.FromMinutes(1) };
        connection.Start();
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var serialized = connection.Feedback.CreateDraft("1.2.3", null).Preview("");
        Assert.DoesNotContain(secret, serialized);
        using var preview = JsonDocument.Parse(serialized);
        Assert.Equal("transport-error", preview.RootElement.GetProperty("logs")[0].GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task HostEventsRecordNamesWithoutSessionPayloads()
    {
        await using var demo = DemoHost.Start();
        await using var connection = new HostConnection(
            ct => LocalClient.ConnectAsync(demo.Endpoint, TimeSpan.FromSeconds(1), ct),
            new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        connection.Start();
        await Wait.UntilAsync(() => connection.Feedback.CreateDraft("1.2.3", null).Preview("").Contains("host.sessions.synced", StringComparison.Ordinal), "the demo's feedback events");

        var serialized = connection.Feedback.CreateDraft("1.2.3", null).Preview("");
        Assert.Contains("host.sessions.upsert", serialized);
        Assert.DoesNotContain("demo-", serialized);
    }
}
