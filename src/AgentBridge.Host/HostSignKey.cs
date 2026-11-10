using System.Runtime.Versioning;
using AgentBridge.Transport;

namespace AgentBridge.Host;

[SupportedOSPlatform("windows")]
internal sealed class HostSignKey : IDisposable
{
    public byte[] PublicPoint => throw new NotImplementedException();
    public KeyStorage Storage => throw new NotImplementedException();
    public byte[] Sign(byte[] message) => throw new NotImplementedException();
    public static HostSignKey LoadOrCreate(string profile, KeyStorage? prefer = null) => throw new NotImplementedException();
    public void Dispose() { }
}
