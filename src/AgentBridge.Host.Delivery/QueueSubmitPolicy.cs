using System.Collections.Frozen;
using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery;

internal enum DeliveryTerminalKind { Unknown, WindowsTerminal, Conhost, Kitty, NonKitty }

internal static class QueueSubmitPolicy
{
    public static readonly FrozenDictionary<DeliveryTerminalKind, QueueCapability> Capabilities =
        Enum.GetValues<DeliveryTerminalKind>().ToFrozenDictionary(kind => kind, _ => QueueCapability.Unknown);

    public static SubmitKey? Select(DeliveryTerminalKind kind, bool? idle, bool? empty, bool urgent = false)
    {
        if (empty != true || idle is null) { return null; }
        if (urgent) { return SubmitKey.Enter; }
        if (Capabilities.GetValueOrDefault(kind, QueueCapability.Unknown) == QueueCapability.Verified)
        { return SubmitKey.Queue; }
        return idle == true ? SubmitKey.Enter : null;
    }
}
