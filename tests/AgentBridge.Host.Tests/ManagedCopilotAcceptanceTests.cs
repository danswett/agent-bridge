using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Mac;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

[CollectionDefinition("Real Mac agent acceptance", DisableParallelization = true)]
public sealed class RealMacAgentAcceptanceCollection;

public sealed class RealMacCopilotFactAttribute : FactAttribute
{
    public RealMacCopilotFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENT_BRIDGE_RUN_REAL_COPILOT") != "1")
        { Skip = "opt in explicitly: this test starts a real Copilot session and makes four small model turns"; }
        else if (!OperatingSystem.IsMacOS()) { Skip = "requires the macOS managed terminal backend"; }
    }
}

// This is deliberately separate from ordinary CI: real CLI, real transcript discovery,
// and real delivery services. Only the homes and workspace are disposable. The normal
// Copilot authentication path is used; no credentials are copied or printed by the test.
[Collection("Real Mac agent acceptance")]
public sealed class ManagedCopilotAcceptanceTests(ITestOutputHelper output)
{
    [RealMacCopilotFact]
    public async Task Real_Copilot_launch_reply_answer_and_stop_are_confirmed_by_its_own_transcript()
    {
        var probe = new MacAgentHaBridgeProbe();
        Assert.Null(probe.Active());
        var copilot = Environment.GetEnvironmentVariable("AGENT_BRIDGE_COPILOT_EXECUTABLE") ?? "/opt/homebrew/bin/copilot";
        var tmux = Environment.GetEnvironmentVariable("AGENT_BRIDGE_TMUX_EXECUTABLE") ?? "/opt/homebrew/bin/tmux";
        Assert.True(Path.IsPathFullyQualified(copilot) && File.Exists(copilot), "configure an absolute native Copilot executable");
        Assert.True(Path.IsPathFullyQualified(tmux) && File.Exists(tmux), "configure an absolute tmux executable");
        var root = PrivateFolder.Ensure("/private/tmp/ab-real-copilot-" + Guid.NewGuid().ToString("N")[..10]);
        var workspace = PrivateFolder.Ensure(Path.Combine(root, "workspace"));
        var home = PrivateFolder.Ensure(Path.Combine(root, "copilot"));
        var claude = Path.Combine(root, "unused-claude");
        var codex = Path.Combine(root, "unused-codex");
        PrivateFolder.WriteNew(Path.Combine(home, "config.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            disableAllHooks = true,
            trustedFolders = new[] { workspace },
            memory = false,
            ide = new { autoConnect = false },
        }));
        var backend = new ManagedTmuxBackend(tmux, Path.Combine(root, "runtime"));
        var processes = new OwnedProcesses(backend);
        var source = new SessionSource(new SessionSourceOptions
        {
            CopilotHome = home, ClaudeHome = claude, CodexHome = codex,
            ClaudeRegistrationRoots = [], CodexRegistrationRoots = [],
            PollInterval = TimeSpan.FromMilliseconds(250), CoalesceInterval = TimeSpan.Zero,
        }, processes);
        var gate = new DeliveryGate();
        var config = LaunchConfig.Default with
        {
            Workspaces = [new("Acceptance", workspace, false)], DefaultAgent = "copilot",
            Agents = new Dictionary<string, AgentConfig>
            {
                ["copilot"] = new()
                {
                    Path = copilot,
                    ExtraArgs = ["--no-custom-instructions", "--no-remote-export", "--no-auto-update", "--no-bash-env", "--disable-builtin-mcps",
                        "--available-tools=ask_user", "--deny-tool=shell", "--deny-tool=write", "--deny-tool=url", "--disallow-temp-dir"],
                },
            },
        };
        var launcher = new LaunchService(source, probe, backend, new IsolatedCopilotStarter(backend, home),
            new OnlyCopilotLocator(), new AgencyCatalog(new CommandRunner()), new LaunchServiceOptions
            {
                CopilotHome = home, ClaudeHome = claude, CodexHome = codex,
                Config = config, PlatformSupported = true, MacCompatibility = new MacLaunchCompatibility(),
                // This is a fresh test-owned workspace, explicitly approved above.
                IsSystemDirectory = p => !string.Equals(p, workspace, StringComparison.Ordinal),
                RegistrationWait = TimeSpan.FromSeconds(40), ReadyWait = TimeSpan.FromSeconds(30),
                FollowFor = TimeSpan.FromMinutes(2), PollInterval = TimeSpan.FromMilliseconds(250),
                ScreenAfter = TimeSpan.FromMilliseconds(500), ScreenEvery = TimeSpan.FromMilliseconds(500),
            });
        var replier = new ReplyService(source, processes, probe, backend, backend, new ReplyOptions
        {
            CopilotHome = home, PlatformSupported = true, ConfirmTimeout = TimeSpan.FromSeconds(30),
        }, gate: gate);
        var answerer = new AnswerService(source, processes, probe, backend, backend, new AnswerOptions
        {
            CopilotHome = home, PlatformSupported = true, ConfirmTimeout = TimeSpan.FromSeconds(30),
        }, gate: gate);
        var stopper = new StopService(source, processes, probe, backend, backend, new StopOptions
        {
            CopilotHome = home, PlatformSupported = true, Grace = TimeSpan.FromSeconds(8), TerminateWait = TimeSpan.FromSeconds(5),
        }, gate: gate);
        var succeeded = false;
        try
        {
            await source.StartAsync();
            var marker = "BRIDGE-MAC-OPEN-" + Guid.NewGuid().ToString("N")[..8];
            var launched = await launcher.LaunchAsync(new LaunchRequest
            {
                Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "Acceptance",
                Prompt = $"Reply with exactly {marker}. Do not use tools, read files, or perform any other action.",
            }, "acceptance");
            Assert.True(launched.Outcome == LaunchOutcome.Started && launched.Session is not null,
                await FailureAsync(backend, root, $"Launch outcome: {launched.Outcome}. {launched.Reason}"));
            var id = launched.Session!;
            await WaitForResponseAsync(source, id, marker, root);
            Assert.True(source.ProcessOf(id) is { } bound && backend.Refuse(bound.Id) is null, "discovery did not bind the real session to its owned process");

            var second = "BRIDGE-MAC-REPLY-" + Guid.NewGuid().ToString("N")[..8];
            var request = new ReplyRequest
            {
                Request = Guid.NewGuid().ToString(), Id = id,
                Text = $"Reply with exactly {second}. Do not use tools, read files, or perform any other action.",
            };
            var reply = await replier.ReplyAsync(request, "acceptance");
            Assert.True(reply.Outcome == AnswerOutcome.Delivered, "the real Copilot transcript did not confirm reply delivery");
            Assert.Equal(reply, await replier.ReplyAsync(request, "acceptance"));
            await WaitForResponseAsync(source, id, second, root);
            Assert.Equal(1, CountPrompts(Path.Combine(home, "session-state", id, "events.jsonl"), request.Text));

            var questionMarker = "BRIDGE-MAC-QUESTION-" + Guid.NewGuid().ToString("N")[..8];
            var answerMarker = "BRIDGE-MAC-ANSWER-" + Guid.NewGuid().ToString("N")[..8];
            var completeMarker = "BRIDGE-MAC-COMPLETE-" + Guid.NewGuid().ToString("N")[..8];
            var ask = await replier.ReplyAsync(new ReplyRequest
            {
                Request = Guid.NewGuid().ToString(), Id = id,
                Text = $"Call ask_user exactly once, with message '{questionMarker}: Please type {answerMarker}.' "
                    + "Use a plain free-text question, with no choices and no requestedSchema. "
                    + $"After receiving my answer, reply with exactly {completeMarker}. Do not use any other tool or read any file.",
            }, "acceptance");
            Assert.True(ask.Outcome == AnswerOutcome.Delivered, "the transcript did not confirm the harmless question request");
            var question = await WaitForQuestionAsync(source, id, questionMarker, root);
            Assert.Equal(QuestionKind.Form, question.Kind);
            Assert.Empty(question.Fields);
            Assert.NotEqual(true, question.AnswerAtMachine);
            var answerRequest = new AnswerRequest
            {
                Request = Guid.NewGuid().ToString(), Id = id, Question = question.Id, Text = answerMarker,
            };
            var answer = await answerer.AnswerAsync(answerRequest);
            Assert.True(answer.Outcome == AnswerOutcome.Delivered, "the real Copilot transcript did not confirm the harmless answer: " + answer.Reason);
            Assert.Equal(answer, await answerer.AnswerAsync(answerRequest));
            await WaitForResponseAsync(source, id, completeMarker, root);
            Assert.Null(source.Get(id)?.Question);
            var stopped = await stopper.StopAsync(new() { Request = Guid.NewGuid().ToString(), Id = id });
            if (stopped.Outcome == StopOutcome.Confirm)
            { stopped = await stopper.StopAsync(new() { Request = Guid.NewGuid().ToString(), Id = id, Confirm = stopped.Confirm }); }
            Assert.True(stopped.Outcome == StopOutcome.Ended, "the real managed session was not confirmed ended");
            Assert.Empty(backend.Snapshot());
            succeeded = true;
            output.WriteLine("Real Copilot acceptance passed: launch, transcript-confirmed reply and answer, once-only UUID retries, and stop.");
        }
        finally
        {
            // A failed assertion must not strand a test session. G5 still applies to
            // cleanup: if a legacy writer appeared, leave the pane for the operator.
            if (probe.Active() is null)
            {
                foreach (var session in backend.Snapshot())
                {
                    using var held = backend.Open(session.Started.ProcessId, session.Started.StartedAt).Process;
                    if (held is null) { continue; }
                    held.Terminate();
                    await held.WaitForExitAsync(TimeSpan.FromSeconds(5));
                }
            }
            // Keep failures private for inspection; never echo terminal/auth screens or
            // transcripts into test output. Successful runs need no retained credentials.
            await source.DisposeAsync();
            if (succeeded) { Directory.Delete(root, true); }
            else { output.WriteLine("Private acceptance artifacts retained at " + root); }
        }
    }

    static async Task WaitForResponseAsync(SessionSource source, string id, string marker, string root)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            await source.RefreshAsync();
            if (source.Get(id) is { Status: SessionStatus.Idle } session && session.Activity.Response?.Contains(marker, StringComparison.Ordinal) == true) { return; }
            await Task.Delay(250);
        } while (clock.Elapsed < TimeSpan.FromSeconds(75));
        Assert.Fail("Copilot did not record the expected completed model turn. Private artifacts: " + root);
    }

    static async Task<Question> WaitForQuestionAsync(SessionSource source, string id, string marker, string root)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            await source.RefreshAsync();
            if (source.Get(id)?.Question is { } question && question.Message.Contains(marker, StringComparison.Ordinal)) { return question; }
            await Task.Delay(250);
        } while (clock.Elapsed < TimeSpan.FromSeconds(75));
        throw new Xunit.Sdk.XunitException("Copilot did not record the harmless ask_user question. Private artifacts: " + root);
    }

    static async Task<string> FailureAsync(ManagedTmuxBackend backend, string root, string message)
    {
        foreach (var session in backend.Snapshot())
        {
            var screen = await backend.ReadScreenAsync(session.Started.ProcessId);
            if (screen.Rows?.Any(l => l.Contains("/login", StringComparison.OrdinalIgnoreCase) || l.Contains("sign in", StringComparison.OrdinalIgnoreCase)) == true)
            { return "Needs login: Copilot's normal authentication did not authorize this isolated profile. No credentials were copied. Private artifacts: " + root; }
        }
        return message + " Private artifacts: " + root;
    }

    static int CountPrompts(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var e = json.RootElement;
            if (e.TryGetProperty("type", out var type) && type.GetString() == "user.message"
                && e.TryGetProperty("data", out var data) && data.TryGetProperty("content", out var content) && content.GetString() == text) { count++; }
        }
        return count;
    }

    sealed class IsolatedCopilotStarter(ManagedTmuxBackend backend, string home) : IAgentStarter
    {
        StartSpec Isolated(StartSpec spec) => spec with
        {
            Environment = new Dictionary<string, string>(spec.Environment ?? new Dictionary<string, string>()) { ["COPILOT_HOME"] = home },
        };
        public string? Unavailable() => backend.Unavailable();
        public string? CommandLine(StartSpec spec, out string? problem) => backend.CommandLine(Isolated(spec), out problem);
        public Started Start(StartSpec spec) => backend.Start(Isolated(spec));
        public bool Running(Started started) => backend.Running(started);
        public bool? Owns(Started started, int processId) => backend.Owns(started, processId);
    }

    sealed class OnlyCopilotLocator : IAgentLocator
    {
        readonly AgentLocator locator = new();
        public string? Find(Launcher launcher, AgentConfig config) => launcher.Id == "copilot" ? locator.Find(launcher, config) : null;
    }

    sealed class OwnedProcesses(ManagedTmuxBackend backend) : IProcessInspector
    {
        readonly SystemProcessInspector system = new();
        public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) => system.GetAgentProcesses(agent).Where(p => backend.Refuse(p.Id) is null).ToArray();
    }
}
