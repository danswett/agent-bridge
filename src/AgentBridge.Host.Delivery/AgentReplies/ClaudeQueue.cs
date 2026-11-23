using System.Text;
using System.Text.Json;

namespace AgentBridge.Host.Delivery.AgentReplies;

// Where Claude's transcript stands, and whether Claude has taken a relay prompt (docs/message-delivery.md "Busy sends to Claude", #3856).
public static class ClaudeQueue
{
    public static long Mark(string path) => 0;
    public static bool Taken(string path, long from, string requestId) => false;
}