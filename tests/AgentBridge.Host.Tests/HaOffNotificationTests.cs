using System.Text.RegularExpressions;
using Xunit;

// HA-off evidence for notifications (parity row 16, docs/design/ha-s4-evidence-matrix.md): the code that sends a
// question or finished-turn notice to a PWA phone and shows one in the Windows app names no Home Assistant type,
// API path or notify service, so turning Home Assistant off cannot take either away. The flow itself, with Home
// Assistant pointed at a listener nothing may call, is PushFlowTests.A_question_and_a_finished_turn_reach_the_phone_...
public sealed class HaOffNotificationTests
{
    // A Home Assistant type or namespace, its REST API, or a phone notify service.
    static readonly Regex HomeAssistant = new(
        @"AgentBridge\.HomeAssistant|\bHomeAssistant\b|homeassistant|/api/(states|services)|notify\.mobile_app|mobile_app_|HaConnector",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The host's push sender and subscription store, the notice payload, the PWA's registration and worker, and the
    // Windows app's toasts and what answers them.
    static readonly string[] Paths =
    [
        "src/AgentBridge.Host/PushNotifier.cs",
        "src/AgentBridge.Host/PushStore.cs",
        "src/AgentBridge.Protocol/Push.cs",
        "pwa/js/push.js",
        "pwa/sw.js",
        "src/AgentBridge.App/Toasts.cs",
        "src/AgentBridge.App.Core/Toasts.cs",
        "src/AgentBridge.App.Core/ToastResponder.cs",
    ];

    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentBridge.slnx"))) { dir = dir.Parent; }
        return dir?.FullName ?? throw new InvalidOperationException("the repository root (AgentBridge.slnx) was not found");
    }

    [Fact]
    public void The_push_and_toast_code_names_no_Home_Assistant_type_api_or_notify_service()
    {
        var root = Root();
        var named = new List<string>();
        foreach (var path in Paths)
        {
            var full = Path.Combine(root, path);
            Assert.True(File.Exists(full), $"{path} is part of the notification paths this test guards: it moved or went; update the list");
            var text = File.ReadAllText(full);
            foreach (Match m in HomeAssistant.Matches(text)) { named.Add($"{path}: {m.Value}"); }
        }
        Assert.Empty(named);
    }
}
