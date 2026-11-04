using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AgentBridge.Transport.Tests;

public class SignInTests
{
    sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    [Fact]
    public async Task OnlyTheExactDevicePageReachesThePersonSigningIn()
    {
        var rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "device-pages.json")));
        foreach (var row in rows.RootElement.EnumerateArray())
        {
            var uri = row.GetProperty("uri").GetString();
            using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK) {
                Content = JsonContent.Create(new { verificationUri = uri, userCode = "ABCD", deviceCode = "fixture", interval = 1, expiresIn = 90 })
            }));
            using var cancellation = new CancellationTokenSource();
            string? displayed = null;
            var flow = SignIn.DeviceFlowAsync(http, new Uri("https://relay.example"), (_, page) => { displayed = page; cancellation.Cancel(); }, cancellation.Token);
            if (row.GetProperty("valid").GetBoolean())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow);
                Assert.Equal(uri, displayed);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => flow);
                Assert.Null(displayed);
            }
        }
    }

    static Dictionary<string, object?> Device() => new() {
        ["verificationUri"] = SignIn.DevicePage, ["userCode"] = "ABCD", ["deviceCode"] = "fixture", ["interval"] = 1, ["expiresIn"] = 90
    };

    [Theory]
    [InlineData("userCode", " ")]
    [InlineData("deviceCode", "")]
    [InlineData("expiresIn", 0)]
    [InlineData("expiresIn", 3601)]
    [InlineData("interval", -1)]
    [InlineData("interval", 61)]
    [InlineData("interval", 1.5)]
    [InlineData("interval", "1")]
    public async Task Invalid_start_fields_never_display_a_code(string field, object value)
    {
        var device = Device();
        device[field] = value;
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = JsonContent.Create(device) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SignIn.DeviceFlowAsync(http, new Uri("https://relay.example"),
            (_, _) => Assert.Fail("Invalid grant displayed")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"pending\":true,\"interval\":0}")]
    [InlineData("{\"pending\":true,\"interval\":61}")]
    [InlineData("{\"pending\":true,\"interval\":null}")]
    public async Task Malformed_pending_responses_stop_polling(string pending)
    {
        int count = 0;
        using var http = new HttpClient(new Handler(() => ++count switch {
            1 => new(HttpStatusCode.OK) { Content = JsonContent.Create(Device()) },
            2 => new(HttpStatusCode.Accepted) { Content = new StringContent(pending) },
            _ => throw new Exception("Unexpected extra poll")
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SignIn.DeviceFlowAsync(http, new Uri("https://relay.example"), (_, _) => { }));
        Assert.Equal(2, count);
    }
}
