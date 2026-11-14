using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class LoadSamplingTests
{
    sealed class Clock : FakeTimeProvider
    {
        public readonly Channel<TimeSpan> Armed = Channel.CreateUnbounded<TimeSpan>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Armed.Writer.TryWrite(dueTime);
            return timer;
        }
    }
    sealed class Table : IProcessTable
    {
        public IReadOnlyList<ProcessReading> Read() => [];
    }

    [Theory]
    [InlineData(true, false, 5)]
    [InlineData(false, true, 5)]
    [InlineData(false, false, 60)]
    public async Task Disabled_relay_sharing_preserves_HA_and_local_session_collection(bool ha, bool session, int seconds)
    {
        using var f = new Fixture();
        if (session)
        {
            f.CopilotSession("88888888-8888-4888-8888-888888888888", 100);
            f.Processes.Run(AgentKind.Copilot, 100);
        }
        await using var source = new SessionSource(f.Options(), f.Processes, f.Time);
        await source.RefreshAsync();
        var clock = new Clock();
        var settings = HostSettings.Default with
        {
            LoadEnabled = false, HomeAssistant = HostSettings.Default.HomeAssistant with { Enabled = ha },
        };
        await using var sampler = HostLoadSampling.Create(settings, source, new Table(), clock);
        sampler.Start();
        Assert.Equal(TimeSpan.FromSeconds(seconds), await clock.Armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
