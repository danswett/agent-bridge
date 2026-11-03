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
}
