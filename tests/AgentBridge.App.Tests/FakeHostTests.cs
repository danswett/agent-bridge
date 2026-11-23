using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// The fake host's "stops when asked": the host stops once its answer to Stop has gone, as the real host does (HostControl), so a test
// no longer guesses a delay (a 50 ms sleep) between the answer and the stop (#2558). `asked` sees the request as it arrives.
public sealed class FakeHostTests
{
    [Fact]
    public async Task A_host_that_stops_when_asked_notes_the_request_answers_it_intact_and_only_then_goes()
    {
        // Many times over, as under load: the host answers and hangs up at once, so the client may read the answer and the end of the stream while its
        // own write is still completing (LocalChannel, #4026): that once failed this call in about 1 run of 11 of these 200-round loops.
        for (var round = 0; round < 200; round++)
        {
            await using var host = new FakeHost();
            var asked = 0;
            host.StopsWhenAsked(_ => Interlocked.Increment(ref asked));
            host.Start();
            await using var channel = await LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(5));

            var answer = await channel.CallAsync(HostOps.Stop);

            Assert.True(answer.GetProperty("stopping").GetBoolean(), $"round {round}: the answer was not intact");
            Assert.Equal(1, asked);
            Assert.Same(channel.Closed, await Task.WhenAny(channel.Closed, Task.Delay(FakeHost.Patience)));
        }
    }
}

