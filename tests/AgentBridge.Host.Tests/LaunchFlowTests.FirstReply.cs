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
    async Task<(SecureChannel Launching, SecureChannel Other, LaunchService Launcher)> ServeWithRepliesAsync(LaunchConfig config, TimeSpan? readyWait = null, TimeSpan? follow = null)
    {
        var launcher = Launcher(config, readyWait: readyWait, follow: follow);
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

    [Theory]
    [InlineData("unreadable")]
    [InlineData("folder-trust")]
    [InlineData("startup-question")]
    [InlineData("never-ready")]
    public async Task A_reply_to_a_session_still_starting_is_refused_with_the_starting_code_and_the_words_as_fallback(string kind)
    {
        screens.Rows = kind switch
        {
            "folder-trust" => TrustRows,
            "startup-question" => ["Hooks need review"],
            _ => [],
        };
        var (launching, other, _) = await ServeWithRepliesAsync(Config(), readyWait: TimeSpan.FromMilliseconds(100),
            follow: kind == "never-ready" ? TimeSpan.FromSeconds(1) : null);
        await LaunchAsync(launching, new { agent = "copilot", workspace = "App" });
        var id = Assert.Single(starter.Specs).Environment![Launchers.SessionVariable]!;
        if (kind == "never-ready")
        {
            await WaitWhileClockRunsAsync(() => Task.FromResult(logged.Any(l => l.Contains("wait until a prompt is typed at the machine", StringComparison.Ordinal))));
        }

        var reply = await RequestAsync(other, Ops.SessionReply, new { id, request = Guid.NewGuid().ToString(), text = "hello from the phone" });

        Assert.Equal("refused", reply.GetProperty("outcome").GetString());
        Assert.Equal(kind switch
        {
            "folder-trust" => FirstReplyWords.AskingTrust("Copilot", false),
            "startup-question" => FirstReplyWords.AskingAtMachine("Copilot"),
            "never-ready" => FirstReplyWords.NotSeenReady("Copilot"),
            _ => FirstReplyWords.StillStarting("Copilot", false),
        }, reply.GetProperty("reason").GetString());
        Assert.Empty(screens.Typed);
        Assert.Equal("starting", reply.GetProperty("code").GetString());
        Assert.Equal(kind, reply.GetProperty("subKind").GetString());
    }

    [Fact]
    public async Task A_starting_refusal_is_remembered_after_ready_and_a_fresh_retry_checks_the_full_gate()
    {
        screens.Rows = TrustRows;
        var (launching, other, launcher) = await ServeWithRepliesAsync(Config());
        var asking = await LaunchAsync(launching, new { agent = "copilot", workspace = "App" });
        var request = new { id = asking.Session!, request = Guid.NewGuid().ToString(), text = "hello from the phone" };
        var refused = await RequestAsync(other, Ops.SessionReply, request);
        Assert.Equal("starting", refused.GetProperty("code").GetString());
        screens.Rows = CopilotReady;
        await WaitAsync(() => Task.FromResult(launcher.HeldForReply(asking.Session!) is null));
        Assert.Empty(screens.Typed);

        var replay = await RequestAsync(other, Ops.SessionReply, request);
        Assert.Equal(refused.GetRawText(), replay.GetRawText());
        probe.Reason = "agent-ha-bridge is running for this user";
        var retry = await RequestAsync(other, Ops.SessionReply, new { request.id, request = Guid.NewGuid().ToString(), request.text });
        Assert.Equal("refused", retry.GetProperty("outcome").GetString());
        Assert.Equal(probe.Reason, retry.GetProperty("reason").GetString());
        Assert.False(retry.TryGetProperty("code", out _));
        Assert.False(retry.TryGetProperty("subKind", out _));
        Assert.Empty(screens.Typed);
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

        Assert.Equal(("refused", fromDevices && approved
            ? "Copilot is asking whether to trust its folder. Answer that first, then send this again"
            : "Copilot is asking whether to trust its folder, and a reply would answer it. Answer it on that machine"), reply);
        Assert.Empty(screens.Typed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_session_whose_window_is_not_drawn_yet_holds_a_reply_as_still_starting_and_types_nothing(bool fromDevices, bool approved)
    {
        screens.Rows = [];
        var (launching, other, _) = await ServeWithRepliesAsync(Config(c => c with { RemoteFolderTrust = fromDevices, RemoteFolderTrustDevices = approved ? ["a-device"] : [] }), readyWait: TimeSpan.FromMilliseconds(200));
        var unknown = await LaunchAsync(launching, new { agent = "copilot", workspace = "App" });
        Assert.Equal("unconfirmed", unknown.Outcome_);

        var reply = await ReplyFromAsync(other, unknown.Session!, "hello from the phone");

        // Needs you is named only where the question would show there with a way to answer it.
        var needsYou = fromDevices && approved;
        Assert.Equal(("refused", needsYou
            ? "Copilot is still starting, so it can't take a reply yet. If it asks whether to trust its folder, that shows in Needs you. Otherwise, send it again in a moment"
            : "Copilot is still starting, so it can't take a reply yet. Send it again in a moment"), reply);
        Assert.Empty(screens.Typed);
    }
}
