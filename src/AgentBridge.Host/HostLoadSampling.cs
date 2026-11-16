using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Load;

namespace AgentBridge.Host;

public static class HostLoadSampling
{
    public static ProcessLoadSampler Create(HostSettings settings, ISessionSource source, IProcessTable table,
        TimeProvider? time = null, bool relay = true)
    {
        var sampler = new ProcessLoadSampler(source, table, time);
        sampler.SetConsumerDemand((relay && settings.LoadEnabled) || settings.HomeAssistant.Enabled);
        return sampler;
    }
}
