namespace AgentBridge.Host.Delivery;

#if INJECTOR
internal
#else
public
#endif
enum SubmitKey { None, Enter, Queue }

internal static class SubmitKeys
{
    public const string Protocol = "agent-bridge-keys 2";

    public static bool TryDecode(string value, out SubmitKey key)
    {
        key = value switch { "1" => SubmitKey.Enter, "2" => SubmitKey.Queue, _ => SubmitKey.None };
        return value is "0" or "1" or "2";
    }
}
