using System.Text;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.Host.Fleet;

public static class FleetRecordSigning
{
    public const string Context = "agent-bridge fleet registry v1";

    public static byte[] CanonicalBytes(JsonElement record) => Encoding.UTF8.GetBytes(record.GetRawText());

    public static byte[] SignedBytes(JsonElement record) => Framing.F(Framing.Label(Context), CanonicalBytes(record));

    public static JsonElement Sign(JsonElement record, Func<byte[], byte[]> signer) => record;

    public static JsonElement SignExisting(JsonElement record, string profile) => record;

    public static bool Verify(JsonElement record, byte[] hostSign) => false;
}
