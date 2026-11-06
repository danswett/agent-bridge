using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// #1018: a session launched with no first prompt takes its first reply from another device,
// unless its window asks whether to trust the folder (Trust's invariant: a reply is never typed
// into a startup question). The launcher, the reply service and the screens are the real ones.
public sealed partial class LaunchFlowTests
{
    static readonly IReadOnlyList<string> TrustRows =
    [
        "\u256D" + new string('\u2500', 20) + "\u256E",
        "\u2502 Confirm folder trust     \u2502",
        "\u2502 Do you trust the files in this folder? \u2502",
        "\u2502 \u276F 1. Yes                \u2502",
        "\u2502   2. Yes, and remember this folder for future sessions \u2502",
        "\u2502   3. No (Esc)               \u2502",
        "\u2502 \u2191/\u2193 to navigate \u00B7 enter to select \u00B7 esc to cancel \u2502",
        "\u2570" + new string('\u2500', 20) + "\u256F",
    ];

    // The launching device and a second one; replies go from the second.
    async Task<(SecureChannel Launching, SecureChannel Other, LaunchService Launcher)> ServeWithRepliesAsync(LaunchConfig config, TimeSpan? readyWait = null)
    {
        var launcher = Launcher(config, readyWait: readyWait);
        var replier = new ReplyService(source, f.Processes, probe, new AnyTarget(), screens, new ReplyOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            ConfirmTimeout = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMilliseconds(25),
        }, gate: new DeliveryGate()) { LaunchHeld = launcher.HeldForReply };
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), launcher: launcher, replies: replier).HandleAsync;
        var launching = await OpenAsync();
        var tablet = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "Tablet", "pwa");
        await tablet.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return (launching, await tablet.Hub.OpenAsync(host.Id), launcher);
    }

    static async Task<(string Outcome, string? Reason)> ReplyFromAsync(SecureChannel ch, string session, string text)
    {
        var r = await RequestAsync(ch, Ops.SessionReply, new { id = session, request = Guid.NewGuid().ToString(), text });
        return (r.GetProperty("outcome").GetString()!, r.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
    }

    [Fact]
    public async Task A_session_launched_with_no_prompt_into_a_trusted_folder_takes_its_first_reply_from_a_second_device()
    {
        var (launching, other, _) = await ServeWithRepliesAsync(Config());
        var started = await LaunchAsync(launching, new { agent = "copilot", workspace = "App" });
        Assert.Equal("started", started.Outcome_);
        var transcript = Path.Combine(f.CopilotHome, "session-state", started.Session!, "events.jsonl");
        Assert.False(File.Exists(transcript));
        // As the agent does: the first prompt is the first thing it records.
        screens.OnTyped = plan => Fixture.Append(transcript, Copilot.User(plan.Sends[0].Text, 1));

        var reply = await ReplyFromAsync(other, started.Session!, "hello from the phone");

        Assert.Equal(("delivered", null), reply);
        Assert.Single(screens.Typed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_session_launched_into_an_untrusted_folder_shows_its_question_and_holds_a_reply_with_words_for_the_phone(bool fromDevices, bool approved)
    {
        screens.Rows = TrustRows;
        var (launching, other, _) = await ServeWithRepliesAsync(Config(c => c with { RemoteFolderTrust = fromDevices, RemoteFolderTrustDevices = approved ? ["a-device"] : [] }));
        var asking = await LaunchAsync(launching, new { agent = "copilot", workspace = "App" });
        Assert.Equal("unconfirmed", asking.Outcome_);
        var options = await RequestAsync(launching, LaunchProtocol.Options, new { });
        Assert.Equal(1, options.GetProperty("trust").GetArrayLength());

        var reply = await ReplyFromAsync(other, asking.Session!, "hello from the phone");

        Assert.Equal(("refused", FirstReplyWords.AskingTrust("Copilot", answerableFromHere: fromDevices && approved)), reply);
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_session_whose_window_is_not_drawn_yet_holds_a_reply_as_waiting_to_start_and_types_nothing()
    {
        screens.Rows = [];
        var (launching, other, _) = await ServeWithRepliesAsync(Config(), readyWait: TimeSpan.FromMilliseconds(200));
        var unknown = await LaunchAsync(launching, new { agent = "copilot", workspace = "App" });
        Assert.Equal("unconfirmed", unknown.Outcome_);

        var reply = await ReplyFromAsync(other, unknown.Session!, "hello from the phone");

        Assert.Equal(("refused", FirstReplyWords.WaitingToStart), reply);
        Assert.Empty(screens.Typed);
    }
}
