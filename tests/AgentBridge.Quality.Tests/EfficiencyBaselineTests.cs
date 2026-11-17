using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class EfficiencyBaselineTests
{
    [Fact]
    public async Task The_PWA_attaches_to_an_existing_page_and_writes_only_the_requested_artifact()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? key = null;
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    key = line.Split(':', 2)[1].Trim();
            Assert.NotNull(key);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            var buffer = new byte[4096];
            var samples = 0;
            try
            {
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    using var command = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                    var method = command.RootElement.GetProperty("method").GetString();
                    Assert.Contains(method, new[] { "Performance.enable", "Network.enable", "Performance.getMetrics" });
                    object result = method == "Performance.getMetrics"
                        ? new { metrics = new[] { new { name = "TaskDuration", value = (double)++samples }, new { name = "JSHeapUsedSize", value = 4096.0 } } }
                        : new { metrics = Array.Empty<object>() };
                    var data = JsonSerializer.SerializeToUtf8Bytes(new { id = command.RootElement.GetProperty("id").GetInt32(), result });
                    await socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                }
            }
            catch (WebSocketException) { /* The observer disposes its connection, not the browser. */ }
            Assert.True(samples >= 2);
        }, timeout.Token);
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var path = Path.Combine(Path.GetTempPath(), $"efficiency-pwa-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "quality", "efficiency-baseline.ps1"),
                         "-HostProcessId", "2147483647", "-DesktopProcessId", "2147483647", "-DurationSeconds", "1",
                         "-SampleIntervalSeconds", "0.1", "-PwaCdpWebSocket", $"ws://127.0.0.1:{port}/page", "-OutputPath", path })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, (await error) + (await output));
            await server;
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(path, timeout.Token));
            var pwa = artifact.RootElement.GetProperty("targets").GetProperty("pwa");
            Assert.Equal("available", pwa.GetProperty("status").GetString());
            Assert.Equal(4096, pwa.GetProperty("jsHeapUsedBytes").GetInt64());
            Assert.True(pwa.GetProperty("cpuSecondsPerMinute").GetDouble() > 0);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("elapsed")]
    [InlineData("exit")]
    [InlineData("replacement")]
    [InlineData("readonly")]
    [InlineData("no-survivors")]
    [InlineData("partial")]
    [InlineData("ambiguous")]
    [InlineData("metric-values")]
    [InlineData("entrypoint")]
    [InlineData("pwa-metrics")]
    [InlineData("pwa-missing")]
    [InlineData("pwa-exit")]
    [InlineData("pwa-network")]
    [InlineData("pwa-cdp")]
    public async Task The_baseline_contract_holds_for_each_fixture(string scenario)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(root, "quality", "efficiency-baseline.Tests.ps1"), "-Scenario", scenario })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await error) + (await stdout));
    }
}
