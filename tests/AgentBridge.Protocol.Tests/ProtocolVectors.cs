using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

// The shared protocol vectors (docs/protocol/vectors/README.md): what every Protocol v1 record
// looks like when it is right, and what a reader or a host must refuse. This is the one place
// they are written; ProtocolVectorTests fails while the committed files differ from what this
// generates, and writes them with UPDATE_PROTOCOL_VECTORS=1.
public static class ProtocolVectors
{
    public const string Generator = "tests/AgentBridge.Protocol.Tests/ProtocolVectors.cs";

    // Who writes the record: a host for its clients, a client for a host, or one host for another.
    const string Host = "host";
    const string Client = "client";
    const string HostToHost = "host-to-host";

    // Whether a vector's record is read under v1-sessions.md "Malformed records" (Received): one a
    // host sends a client over the channel. Not a push (push.json), and not one host to another.
    public static bool ReadLeniently(string direction, string file) => direction == Host && file != "push.json";

    static readonly DateTimeOffset T0 = new(2026, 10, 28, 4, 22, 43, 182, TimeSpan.Zero);
    static readonly DateTimeOffset T1 = T0.AddMinutes(3);
    const string SessionId = "4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b";
    const string MachineId = "53d1997f0a1b2c3d4e5f60718293a4b5";
    const string OtherMachine = "00112233445566778899aabbccddeeff";
    const string Request = "3d5e7f90-1a2b-4c3d-8e4f-5a6b7c8d9e0f";
    const string FileA = "6b1f0c3e-2d4a-4b5c-9e8f-0a1b2c3d4e5f";
    const string FileB = "7c2e1d4f-3e5b-4c6d-8f90-1b2c3d4e5f60";
    static readonly string Sha = Attachments.Base64Url(SHA256.HashData("hello"u8));
    static readonly string BlobKey = Attachments.Base64Url([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);

    static string Repeat(char c, int n) => new(c, n);

    // A record as written, then broken: the malformed records v1-sessions.md decides (#446).
    static string Broken<T>(T value, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var json = JsonSerializer.SerializeToNode(value, ProtocolJson.Options)!.AsObject();
        change(json);
        return json.ToJsonString();
    }

    public static IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> Files()
    {
        var catalog = new VectorCatalog();
        Sessions(catalog.File("sessions.json", "v1-sessions.md", "Sessions, questions, answers, replies and stopping a session."));
        AttachmentRecords(catalog.File("attachments.json", "v1-sessions.md#attachments", "Files sent ahead of a reply or a launch, in the channel or through the relay's blob store (blobs.md)."));
        Push(catalog.File("push.json", "v1-sessions.md#notifications", "Registering for notifications, and the notices a host sends."));
        Launch(catalog.File("launch.json", "v1-launch.md", "Launching and resuming sessions, and answering a launched agent's folder-trust question."));
        Transfer(catalog.File("transfer.json", "v1-transfer.md", "Bringing a session from another machine, and the signed command that asks for it."));
        Update(catalog.File("update.json", "v1-update.md", "A machine's updates, seen and asked for from another device."));
        Usage(catalog.File("usage.json", "v1-usage.md", "How much of each agent's allowance is gone."));
        foreach (var d in KnownDifferences.All) { catalog.Differs(d); }
        return catalog.Build();
    }

    // ---- sessions ----

    static readonly MachineInfo Machine = new(MachineId, "DEV-VM1", "windows", "0.1.200", Dev: true, Sessions: 3, ReadOnly: "agent-ha-bridge is running for this user", Instance: "9a8b7c6d5e4f30211203f4e5d6c7b8a9");

    static readonly RestoreSession RestoredMidTurn = new() { Id = SessionId, Agent = AgentKind.Copilot, Title = "Fix the login page", MidTurn = true };
    static readonly RestoreSession RestoredAsking = new() { Id = "7a21c3d4-5e6f-4a1b-9c2d-3e4f5a6b7c8d", Agent = AgentKind.Claude, QuestionLost = true };

    static readonly ToolCall Running = new() { Id = "call_7", Name = "powershell", Summary = "Run the protocol tests", Status = ToolStatus.Running, StartedAt = T1 };
    static readonly ToolCall Finished = new() { Id = "call_6", Name = "edit", Summary = "src/AgentBridge.Protocol/Sessions.cs", Status = ToolStatus.Done, StartedAt = T0, EndedAt = T0.AddSeconds(2) };

    static readonly Field Spike = new()
    {
        Name = "spike", Title = "Spike", Description = "Which spike to run next", Type = FieldType.Choice,
        Options = [new("s6", "S6: TPM-backed device keys (Recommended)"), new("s7")], AllowOther = true,
        Default = JsonSerializer.SerializeToElement("s6"), Required = true,
    };
    static readonly Field Parts = new() { Name = "parts", Title = "Parts", Type = FieldType.Multi, Options = [new("host"), new("relay"), new("pwa")], AllowOther = true };
    static readonly Field Confirm = new() { Name = "confirm", Type = FieldType.Boolean };
    static readonly Field Notes = new() { Name = "notes", Type = FieldType.Text };
    static readonly Field Count = new() { Name = "count", Type = FieldType.Number, Default = JsonSerializer.SerializeToElement(3) };

    // A field's schema constraints (#766), each a number JavaScript holds exactly.
    static JsonElement N(double n) => JsonSerializer.SerializeToElement(n);
    static readonly Field Bounded = new() { Name = "runs", Title = "Runs", Type = FieldType.Number, Required = true, Constraints = new() { Minimum = N(2), Maximum = N(10), MultipleOf = N(2), Integer = true } };
    static readonly Field Patterned = new() { Name = "ticket", Type = FieldType.Text, Required = true, Constraints = new() { MinLength = 5, MaxLength = 12, Pattern = "^[A-Z]+-[0-9]+$" } };
    static readonly Field Mail = new() { Name = "mail", Type = FieldType.Text, Constraints = new() { Format = "email" } };
    static readonly Field Day = new() { Name = "day", Type = FieldType.Text, Constraints = new() { Format = "date" } };
    static readonly Field Above = new() { Name = "above", Type = FieldType.Number, Constraints = new() { ExclusiveMinimum = N(0), ExclusiveMaximum = N(1) } };
    static readonly Field Pair = new() { Name = "pair", Type = FieldType.Multi, Options = [new("host"), new("relay"), new("pwa")], Constraints = new() { MinItems = 2, MaxItems = 2 } };
    static readonly Question Limited = new() { Id = "call_q4", Kind = QuestionKind.Form, Message = "Run it", Fields = [Bounded, Patterned, Mail, Day, Above, Pair], AskedAt = T0 };

    static readonly Question Form = new() { Id = "call_q1", Kind = QuestionKind.Form, Message = "Which spike next, and **why**?", Fields = [Spike, Parts, Confirm, Notes, Count], AskedAt = T0 };
    static readonly Question Plain = new() { Id = "call_q2", Kind = QuestionKind.Form, Message = "Which branch should I use?", AskedAt = T0 };
    static readonly Question Approval = new()
    {
        Id = "req-41#3f9a", Kind = QuestionKind.Approval, Message = "Run `git push`?", AskedAt = T0,
        Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new("approve", "Approve once"), new("deny", "Deny")] }],
    };
    static readonly Question ManyTexts = new()
    {
        Id = "call_q3", Kind = QuestionKind.Form, Message = "Notes", AskedAt = T0,
        Fields = [.. Enumerable.Range(1, 5).Select(i => new Field { Name = $"t{i}", Type = FieldType.Text })],
    };

    static readonly Subagent Delegate = new()
    {
        Id = "agent-1", Kind = SubagentKind.Background, Name = "Run the suite", Task = "Run every test and report failures", Model = "claude-haiku-5.5",
        Status = SubagentStatus.Working, StartedAt = T0, Line = "Running the protocol tests", CurrentTool = Running, Shells = 1,
    };
    static readonly Subagent Nested = new() { Id = "agent-2", Parent = "agent-1", Kind = SubagentKind.Task, Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Failed, StartedAt = T0, EndedAt = T1 };

    static readonly Activity FullActivity = new()
    {
        Response = "All **47** tests pass.", ResponseTruncated = true, Reasoning = "The suite is green; summarize.", CurrentTool = Running, History = [Finished],
        UserMessage = "Run the tests", TurnCount = 4, Compactions = 1, Background = new Background { Agents = 1, Shells = 2 },
        Source = new ActivitySource { Reasoning = "agent-1" }, Own = new OwnActivity { Reasoning = "Waiting on the delegate.", At = T0 },
    };

    static readonly Session FullSession = new()
    {
        Id = SessionId, Agent = AgentKind.Copilot, Title = "Protocol vectors", Cwd = @"C:\src\agent-bridge", Repository = "danswett/agent-bridge", Branch = "quality/protocol-vectors",
        Model = "claude-opus-5.5", Status = SessionStatus.Waiting, StatusSince = T1, StartedAt = T0, UpdatedAt = T1, ProcessId = 4120, Activity = FullActivity,
        Question = Form, Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:0f1e2d3c", Subagents = [Delegate, Nested], SubagentsMore = 2, WaitingOn = null,
    };
    // #800: VM1's sign-in failure as a host shows it, with the sentence the host composes (#841).
    static readonly Attention SignInNeeded = new() { Kind = AttentionKind.SignInNeeded, Since = T0, Agent = AgentKind.Copilot, Provider = "github", Account = "mona", Message = "Copilot needs you to sign in again (401).", SignIn = SignInWhere.Phone, Recovery = RecoveryState.Checking };
    static readonly Session MinimalSession = new() { Id = "b7e1c2d3-0000-4000-8000-000000000001", Agent = AgentKind.Claude, Status = SessionStatus.Idle, StatusSince = T0, UpdatedAt = T0, Activity = new Activity() };

    static readonly DiscoveryHeld Held = new("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot]);

    static readonly SubscribeSince Since = new() { Instance = Machine.Instance!, Revision = 118 };

    // session.patch: each valid patch is made by SessionPatches.Diff from its context's session to
    // its result, and the check applies it to the session (held from the event at seq) and must give
    // the result exactly. The recorded sequences are in session-patches.json.
    static readonly Subagent Third = new() { Id = "agent-3", Kind = SubagentKind.Background, Name = "Check the docs", Status = SubagentStatus.Working, StartedAt = T1, CurrentTool = Running with { Id = "call_9" }, Shells = 0 };
    static readonly Session Working = FullSession.WithoutHistory() with { Status = SessionStatus.Working, Question = null, Subagents = [Delegate, Nested, Third] };

    static void Patches(VectorFileBuilder f)
    {
        var ended = Working with
        {
            Status = SessionStatus.Idle, StatusSince = T1.AddSeconds(30), UpdatedAt = T1.AddSeconds(30),
            Activity = Working.Activity with { CurrentTool = null, TurnCount = 5, Response = Working.Activity.Response + " The docs build too." },
            Subagents = [Third, Delegate with { Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Done, EndedAt = T1.AddSeconds(30), CurrentTool = null, Shells = 0 }],
        };
        var asked = Working with { Status = SessionStatus.Waiting, StatusSince = T1.AddSeconds(5), UpdatedAt = T1.AddSeconds(5), Question = Approval };
        var added = Working with { UpdatedAt = T1.AddSeconds(9), Subagents = [.. Working.Subagents!, new Subagent { Id = "agent-4", Kind = SubagentKind.Task, Parent = "agent-3", Status = SubagentStatus.Working, StartedAt = T1.AddSeconds(9) }] };

        JsonObject Json(Session s) => SessionPatches.Json(s);
        SessionPatch Diff(Session to) => SessionPatches.Diff(Json(Working), 41, Json(to))!;
        f.Context("held-at-41-then-a-turn-ending", new { session = Json(Working), seq = 41, result = Json(ended) })
            .Context("held-at-41-then-a-question", new { session = Json(Working), seq = 41, result = Json(asked) })
            .Context("held-at-41-then-a-nested-delegate", new { session = Json(Working), seq = 41, result = Json(added) })
            .Context("none-held-then-the-whole-session", new { result = Json(Working) })
            .Context("held-at-40", new { session = Json(Working), seq = 40 })
            .Context("held-at-41", new { session = Json(Working), seq = 41 })
            .Context("none-held", new { });
        var turn = Diff(ended) with { Revision = 131 };
        f.Record<SessionPatch>(Host, VectorChecks.Patches)
            .Valid("a-turn-ending", turn, "held-at-41-then-a-turn-ending")
            .Valid("a-question-asked", Diff(asked), "held-at-41-then-a-question")
            .Valid("a-delegate-added-at-the-end", Diff(added), "held-at-41-then-a-nested-delegate")
            .Valid("the-whole-session", SessionPatches.Whole(Json(Working), 131), "none-held-then-the-whole-session")
            .Refused("on-another-event-than-the-one-held", turn, "held-at-40")
            .Refused("on-a-session-not-held", turn, "none-held")
            .Refused("for-another-session", turn with { Id = MinimalSession.Id }, "held-at-41")
            .Refused("a-set-that-is-not-an-object", turn with { Set = JsonSerializer.SerializeToElement(new[] { 1 }) }, "held-at-41")
            .Refused("text-appended-to-a-number", turn with { Append = JsonSerializer.SerializeToElement(new { activity = new { turnCount = "0" } }) }, "held-at-41")
            .Refused("a-delegate-ordered-twice", turn with { Subagents = turn.Subagents! with { Order = ["agent-3", "agent-3"] } }, "held-at-41")
            .Refused("a-delegate-without-an-id", turn with { Subagents = turn.Subagents! with { Set = [JsonSerializer.SerializeToElement(new { status = "ended" })] } }, "held-at-41")
            // Read with its base left out, a patch would be applied to nothing as if it were the whole session.
            .Refused("a-base-that-is-not-a-number", Broken(turn, j => j["base"] = "41"));
        f.Record<SubagentsPatch>(Host)
            .Valid("by-id", turn.Subagents!)
            .Valid("one-entry", new SubagentsPatch { Set = [JsonSerializer.SerializeToElement(new { id = "agent-3", status = "ended" })] });
    }

    static void Sessions(VectorFileBuilder f)
    {
        f.Record<MachineInfo>(Host)
            .Valid("full", Machine)
            .Valid("minimal", new MachineInfo(MachineId, "DEV-MAC", "macos", "0.1.200"));
        f.Record<HelloResult>(Host)
            .Valid("full", new HelloResult(1, Machine, [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsReply, Capabilities.SessionsStop, Capabilities.Push, Capabilities.SessionsAttach, Capabilities.SessionsAttachBlob, Capabilities.SessionsApprove, LaunchProtocol.Capability, UsageProtocol.Capability, UpdateProtocol.Capability]))
            .Valid("read-only", new HelloResult(1, new MachineInfo(MachineId, "DEV-LINUX", "linux", "0.1.200"), [Capabilities.SessionsRead]))
            .Reads("a-capability-from-a-newer-host", $$"""{"protocol":1,"machine":{"id":"{{MachineId}}","name":"DEV-VM1","os":"windows","hostVersion":"0.2.0"},"capabilities":["sessions.read","sessions.teleport"],"region":"eu"}""");
        f.Record<ToolCall>(Host)
            .Valid("done", Finished)
            .Valid("running", Running with { Summary = null })
            .Reads("an-unknown-status-reads-as-done", """{"id":"call_8","name":"Bash","status":"queued","startedAt":"2026-10-28T04:22:43.182Z"}""");
        f.Record<Option>(Host)
            .Valid("labelled", new Option("approve", "Approve once"))
            .Valid("value-only", new Option("s7"));
        f.Record<Field>(Host)
            .Valid("choice", Spike)
            .Valid("text", Notes)
            .Valid("number", Count)
            .Valid("a-number-with-constraints", Bounded)
            .Valid("text-with-constraints", Patterned)
            .Valid("a-list-with-item-counts", Pair)
            .Reads("an-unknown-type-reads-as-text", """{"name":"when","type":"date"}""");
        f.Record<FieldConstraints>(Host)
            .Valid("number-bounds", Bounded.Constraints!)
            .Valid("exclusive-bounds", Above.Constraints!)
            .Valid("text-length-and-pattern", Patterned.Constraints!)
            .Valid("a-format", Mail.Constraints!)
            .Valid("item-counts", Pair.Constraints!);
        f.Record<Question>(Host)
            .Valid("form", Form)
            .Valid("plain", Plain)
            .Valid("approval", Approval)
            .Valid("truncated", Plain with { Truncated = true, Fields = [] })
            .Valid("a-delegates-question-answered-at-the-machine", Plain with { AnswerAtMachine = true, Agent = "agent-1" })
            .Reads("an-unknown-kind-reads-as-a-form", """{"id":"q","kind":"wizard","message":"m","fields":[],"askedAt":"2026-10-28T04:22:43.182Z"}""")
            // Never shown with a choice left out, or a choice without the words it was asked with.
            .Refused("a-choice-that-cannot-be-read", Broken(Approval, j => j["fields"]![0]!["options"]!.AsArray().Add(null)))
            .Refused("a-choice-label-that-is-not-text", Broken(Approval, j => j["fields"]![0]!["options"]![0]!["label"] = true))
            .Refused("a-delegate-that-is-not-an-id", Broken(Plain with { Agent = "agent-1" }, j => j["agent"] = 1));
        f.Record<Background>(Host)
            .Valid("agents-and-shells", new Background { Agents = 2, Shells = 1 })
            .Valid("shells-only", new Background { Agents = 0, Shells = 3 });
        f.Record<ActivitySource>(Host)
            .Valid("both", new ActivitySource { Response = "agent-1", Reasoning = "agent-2" })
            .Valid("reasoning-only", new ActivitySource { Reasoning = "agent-1" });
        f.Record<OwnActivity>(Host)
            .Valid("full", new OwnActivity { Response = "Main agent's answer", ResponseTruncated = true, Reasoning = "Main agent's thought", At = T0 })
            .Valid("response-only", new OwnActivity { Response = "Main agent's answer", At = T0 });
        f.Record<Activity>(Host)
            .Valid("full", FullActivity)
            .Valid("empty", new Activity())
            // Who wrote the latest prompt when it was not the person (#489).
            .Valid("a-scheduled-prompt", new Activity { UserMessage = "[Scheduled prompt #4] Check CI and report.", UserMessageFrom = UserMessageFrom.Schedule, UserMessageLabel = "4", TurnCount = 3 })
            .Valid("a-prompt-an-agent-sent", new Activity { UserMessage = "Carry on with the next step.", UserMessageFrom = UserMessageFrom.Agent })
            .Reads("an-unknown-sender-reads-as-other-never-the-person", """{"userMessage":"Carry on.","userMessageFrom":"robot"}""")
            .Refused("a-sender-that-is-not-text", """{"userMessage":"Carry on.","userMessageFrom":4}""");
        f.Record<Subagent>(Host)
            .Valid("working", Delegate)
            .Valid("ended", Nested)
            .Valid("in-full-from-session-get", Delegate with { Status = SubagentStatus.Idle, Outcome = SubagentOutcome.Done, EndedAt = T1, CurrentTool = null, Shells = 0, Response = "Two failures.", ResponseTruncated = true, Reasoning = "Summing up", History = [Finished] })
            .Reads("unknown-kind-status-and-outcome", """{"id":"agent-9","kind":"swarm","status":"sleeping","outcome":"exploded","shells":0}""");
        f.Record<Attention>(Host)
            .Valid("sign-in-needed", SignInNeeded)
            .Valid("an-error-with-a-retry", new Attention { Kind = AttentionKind.Error, Since = T0, Agent = AgentKind.Claude, Message = "Claude Code hit a rate limit (429).", ErrorClass = ErrorClass.Rate, RetryAt = T1 })
            .Valid("not-responding", new Attention { Kind = AttentionKind.NotResponding, Since = T0, Agent = AgentKind.Codex, LastEventAt = T0 })
            // Signed in, and still signInNeeded until the model is seen answering (#800 ruling).
            .Valid("signed-in-waiting-for-the-model", new Attention { Kind = AttentionKind.SignInNeeded, Since = T0, Agent = AgentKind.Copilot, Provider = "github", SignIn = SignInWhere.Phone, Recovery = RecoveryState.Pending })
            .Reads("unknown-values-and-fields-from-a-newer-host", """{"kind":"paused","since":"2026-10-28T04:22:43.182Z","errorClass":"cosmic","signIn":"watch","recovery":"teleporting","somethingNew":{"a":1}}""");
        f.Record<Session>(Host)
            .Valid("full", FullSession)
            .Valid("minimal", MinimalSession)
            .Valid("waiting-on-a-delegate", MinimalSession with { Agent = AgentKind.Copilot, Status = SessionStatus.Waiting, Question = Plain with { Agent = "agent-1", AnswerAtMachine = true }, Subagents = [Delegate with { Status = SubagentStatus.Waiting }], WaitingOn = "agent-1" })
            .Valid("ending", MinimalSession with { Agent = AgentKind.Codex, Status = SessionStatus.Ending })
            // Idle, as its turn ended, with why beside it (#800): an older client reads only that.
            .Valid("signed-out", MinimalSession with { Agent = AgentKind.Copilot, Attention = SignInNeeded })
            .Reads("unknown-values-and-fields-from-a-newer-host", """{"id":"x","agent":"gemini","status":"thinking","driver":"robot","statusSince":"2026-10-28T04:22:43.182Z","updatedAt":"2026-10-28T04:22:43.182Z","activity":{"mood":"good"},"somethingNew":{"a":1}}""")
            // A field that cannot be read is absent, and a required one its default, as the reads say.
            .Reads("a-title-that-is-not-text-and-no-status", Broken(MinimalSession with { Title = "Fix the build" }, j => { j["title"] = 5; j.Remove("status"); }))
            // A question that cannot be read is dropped; its session is kept.
            .Reads("a-question-whose-message-is-not-text", Broken(MinimalSession with { Status = SessionStatus.Waiting, Question = Plain }, j => j["question"]!["message"] = 7))
            // Entries of a list that cannot be read are left out, and the list kept.
            .Reads("delegates-that-cannot-be-read", Broken(MinimalSession with { Subagents = [Delegate] }, j => { j["subagents"]!.AsArray().Add(null); j["subagents"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["kind"] = "background", ["status"] = "working" }); }))
            // Who wrote the text, unreadable: the activity is not shown, rather than a delegate's
            // words shown as the main agent's.
            .Reads("a-source-that-cannot-be-read", Broken(MinimalSession with { Activity = new Activity { Response = "Bisecting.", Source = new ActivitySource { Response = "agent-1" } } }, j => j["activity"]!["source"]!["response"] = 7))
            .Refused("an-id-that-is-not-text", Broken(MinimalSession, j => j["id"] = 7));
        f.Record<DiscoveryHeld>(Host)
            .Valid("one-agent", Held)
            .Valid("every-agent", new DiscoveryHeld("the process list could not be read", [AgentKind.Copilot, AgentKind.Claude, AgentKind.Codex]));
        f.Record<SessionList>(Host)
            .Valid("a-page-with-more", new SessionList([FullSession.WithoutHistory(), MinimalSession], 118, Next: "c2Vzc2lvbnM6Mg", DiscoveryHeld: Held))
            .Valid("the-last-page", new SessionList([MinimalSession], 118))
            .Valid("no-sessions", new SessionList([], 7));
        f.Record<DiscoveryChanged>(Host)
            .Valid("held", new DiscoveryChanged(Held))
            .Valid("released", new DiscoveryChanged());
        // What a reboot or a crash took (#780; v1-sessions.md, What went).
        f.Record<RestoreSession>(Host)
            .Valid("mid-turn", RestoredMidTurn)
            .Valid("a-question-lost", RestoredAsking);
        f.Record<RestoreNotice>(Host)
            .Valid("after-a-reboot", new RestoreNotice { At = T1, Cause = RestoreCause.Reboot, Since = T0, Why = RestoreWhy.Pending, Offered = [RestoredMidTurn, RestoredAsking] })
            .Valid("still-judging-on-a-read-only-machine", new RestoreNotice { At = T1, Cause = RestoreCause.Crash, Waiting = 2, Why = RestoreWhy.ReadOnly, Offered = [RestoredAsking], OfferedMore = 3 });
        f.Record<SessionRestored>(Host)
            .Valid("after-a-crash", new SessionRestored { At = T1, Cause = RestoreCause.Crash })
            .Valid("mid-turn-with-a-question-lost", new SessionRestored { At = T1, Cause = RestoreCause.Reboot, MidTurn = true, QuestionLost = true });
        f.Record<ListRequest>(Client)
            .Valid("first-page", new ListRequest())
            .Valid("next-page", new ListRequest("c2Vzc2lvbnM6Mg"));
        f.Record<SubscribeRequest>(Client)
            .Valid("patches-and-resuming", new SubscribeRequest { Patch = true, Since = Since })
            .Valid("patches-only", new SubscribeRequest { Patch = true })
            .Valid("as-before", new SubscribeRequest());
        f.Record<SubscribeSince>(Client).Valid("a-list-held-at-118", Since);
        f.Record<FocusRequest>(Client)
            .Valid("a-session-open", new FocusRequest { Visible = true, Open = SessionId })
            .Valid("hidden", new FocusRequest { Visible = false });
        f.Record<SubscribeResult>(Host)
            .Valid("three-sessions", new SubscribeResult(118, 3))
            .Valid("resumed-with-one-changed", new SubscribeResult(131, 1, Resumed: true));
        f.Record<SessionsSynced>(Host)
            .Valid("held", new SessionsSynced(118, Held))
            .Valid("plain", new SessionsSynced(118))
            .Valid("resumed", new SessionsSynced(131, Live: [SessionId, MinimalSession.Id]))
            // Ends no snapshot: no session is removed and no hold lifted on a record nobody could read.
            .Refused("not-a-record", "[]")
            // Read with the id left out, a session still running would be dropped.
            .Refused("a-live-id-that-is-not-text", Broken(new SessionsSynced(131, Live: [SessionId]), j => j["live"]!.AsArray().Add(7)));
        f.Record<SessionRemoved>(Host)
            .Valid("gone", new SessionRemoved(SessionId))
            .Valid("gone-to-a-client-of-patches", new SessionRemoved(SessionId, 131));
        Patches(f);

        const string form = "a-form", plain = "a-plain-question", approval = "an-approval", many = "five-text-fields";
        f.Context(form, new { question = Form }).Context(plain, new { question = Plain }).Context(approval, new { question = Approval }).Context(many, new { question = ManyTexts })
            .Context("a-form-with-constraints", new { question = Limited })
            .Context("a-truncated-question", new { question = Plain with { Truncated = true } })
            .Context("an-approval-answered-at-the-machine", new { question = Approval with { AnswerAtMachine = true } });
        // Each answers its context's question by that question's id, as a host requires before it
        // checks anything (VectorChecks holds every answer vector to it).
        AnswerRequest Answer(string context, Dictionary<string, object?>? fields = null, string? text = null) => new()
        {
            Id = SessionId, Question = f.Context(context)["question"]!["id"]!.GetValue<string>(), Request = Request, Text = text,
            Fields = fields?.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value, ProtocolJson.Options)),
        };
        var good = new Dictionary<string, object?> { ["spike"] = "s6", ["parts"] = new[] { "host", "pwa" }, ["confirm"] = true, ["notes"] = "Run it on Linux too", ["count"] = 3 };
        Dictionary<string, object?> With(string name, object? value) => new(good) { [name] = value };
        const string limited = "a-form-with-constraints";
        var Within = new Dictionary<string, object?> { ["runs"] = 4, ["ticket"] = "ABC-12", ["mail"] = "someone@example.com", ["day"] = "2028-02-29", ["above"] = 0.5, ["pair"] = new[] { "host", "pwa" } };
        Dictionary<string, object?> Inside(string name, object? value) => new(Within) { [name] = value };
        f.Record<AnswerRequest>(Client, VectorChecks.Answers)
            .Valid("every-field", Answer(form, good), form)
            .Valid("only-the-required-field", Answer(form, new() { ["spike"] = "s7" }), form)
            .Valid("an-other-answer", Answer(form, With("spike", new { other = "S9: a third relay" })), form)
            .Valid("one-other-among-choices", Answer(form, With("parts", new object[] { "host", new { other = "the docs" } })), form)
            .Valid("a-text-answer", Answer(plain, text: "main"), plain)
            .Valid("a-text-answer-at-its-4-KiB-limit", Answer(plain, text: Repeat('a', Limits.AnswerText)), plain)
            .Valid("an-approval", Answer(approval, new() { ["decision"] = "approve" }), approval)
            .Valid("words-led-by-a-hangul-filler", Answer(plain, text: "\u3164ship it"), plain)
            .Valid("a-joined-emoji", Answer(plain, text: "\U0001F468\u200D\U0001F469\u200D\U0001F467"), plain)
            .Refused("a-truncated-question", Answer("a-truncated-question", text: "main"), "a-truncated-question")
            .Refused("a-question-answered-at-the-machine", Answer("an-approval-answered-at-the-machine", new() { ["decision"] = "approve" }), "an-approval-answered-at-the-machine")
            .Refused("over-16-KiB-in-all", Answer(many, Enumerable.Range(1, 5).ToDictionary(i => $"t{i}", _ => (object?)Repeat('a', 4000))), many)
            .Refused("fields-for-a-text-question", Answer(plain, new() { ["x"] = "y" }), plain)
            .Refused("an-empty-text-answer", Answer(plain, text: " \t "), plain)
            .Refused("only-a-byte-order-mark", Answer(plain, text: "\uFEFF"), plain)
            .Refused("only-a-hangul-filler", Answer(plain, text: "\u3164"), plain)
            .Refused("only-a-variation-selector-and-a-combining-mark", Answer(plain, text: "\uFE0F\u0301"), plain)
            .Refused("only-a-blank-braille-pattern", Answer(plain, text: "\u2800"), plain)
            .Refused("only-default-ignorables", Answer(plain, text: "\u034F\u2065\U000E0000\U000E0100"), plain)
            .Refused("only-an-unassigned-code-point", Answer(plain, text: "\u0378"), plain)
            .Refused("an-other-answer-of-only-a-hangul-filler", Answer(form, With("spike", new { other = "\u3164" })), form)
            .Refused("no-answer-at-all", Answer(plain), plain)
            .Refused("a-text-answer-over-4-KiB", Answer(plain, text: Repeat('a', Limits.AnswerText + 1)), plain)
            .Refused("text-for-a-form", Answer(form, good, "main"), form)
            .Refused("a-field-the-question-lacks", Answer(form, With("colour", "red")), form)
            .Refused("a-required-field-left-out", Answer(form, new() { ["parts"] = new[] { "host" } }), form)
            .Refused("a-required-field-sent-as-null", Answer(form, With("spike", null)), form)
            .Refused("not-one-of-the-choices", Answer(form, With("spike", "s8")), form)
            .Refused("an-other-answer-where-none-is-allowed", Answer(approval, new() { ["decision"] = new { other = "maybe" } }), approval)
            .Refused("an-empty-other-answer", Answer(form, With("spike", new { other = " " })), form)
            .Refused("more-beside-other", Answer(form, With("spike", new { other = "s9", label = "x" })), form)
            .Refused("an-other-answer-over-4-KiB", Answer(form, With("spike", new { other = Repeat('a', Limits.AnswerText + 1) })), form)
            .Refused("two-other-answers", Answer(form, With("parts", new object[] { new { other = "a" }, new { other = "b" } })), form)
            .Refused("a-multi-answer-that-is-not-a-list", Answer(form, With("parts", "host")), form)
            .Refused("a-choice-that-is-a-number", Answer(form, With("spike", 6)), form)
            .Refused("a-boolean-as-text", Answer(form, With("confirm", "yes")), form)
            .Refused("a-number-as-text", Answer(form, With("count", "3")), form)
            .Refused("text-as-a-number", Answer(form, With("notes", 3)), form)
            .Refused("a-text-field-over-4-KiB", Answer(form, With("notes", Repeat('a', Limits.AnswerText + 1))), form)
            .Refused("no-fields-for-a-form-without-required-ones", Answer(approval, new()), approval)
            // #766: a field's schema constraints, checked exactly.
            .Valid("inside-every-constraint", Answer(limited, Within), limited)
            .Refused("a-number-below-its-minimum", Answer(limited, Inside("runs", 0)), limited)
            .Refused("a-number-above-its-maximum", Answer(limited, Inside("runs", 12)), limited)
            .Refused("a-number-that-is-not-whole", Answer(limited, Inside("runs", 4.5)), limited)
            .Refused("a-number-off-its-step", Answer(limited, Inside("runs", 7)), limited)
            .Refused("a-number-at-its-exclusive-bound", Answer(limited, Inside("above", 0)), limited)
            .Refused("text-shorter-than-its-minimum", Answer(limited, Inside("ticket", "AB-1")), limited)
            .Refused("whitespace-that-is-not-an-email-address", Answer(limited, Inside("mail", "   ")), limited)
            .Refused("a-required-text-field-of-only-spaces", Answer(limited, Inside("ticket", "   ")), limited)
            .Refused("a-required-text-field-of-only-control-characters", Answer(limited, Inside("ticket", "\u0085\u001b ")), limited)
            .Refused("a-required-text-field-left-empty", Answer(limited, Inside("ticket", "")), limited)
            .Refused("text-longer-than-its-maximum-in-utf16-units", Answer(limited, Inside("ticket", "ABCDEFGHI-1\U0001F600")), limited)
            .Refused("text-off-its-pattern", Answer(limited, Inside("ticket", "ABC-12x")), limited)
            .Refused("text-that-is-not-an-email-address", Answer(limited, Inside("mail", "someone@example")), limited)
            .Refused("text-that-is-not-a-date", Answer(limited, Inside("day", "2026-02-30")), limited)
            .Refused("too-few-ticked", Answer(limited, Inside("pair", new[] { "host" })), limited)
            .Refused("too-many-ticked", Answer(limited, Inside("pair", new[] { "host", "relay", "pwa" })), limited);
        f.Record<AnswerResult>(Host)
            .Valid("delivered", new AnswerResult(AnswerOutcome.Delivered))
            .Valid("refused", new AnswerResult(AnswerOutcome.Refused, "\"Spike\" needs an answer"))
            .Valid("stale", new AnswerResult(AnswerOutcome.Stale, "the question is no longer pending"))
            .Reads("an-unknown-outcome-reads-as-unconfirmed", """{"outcome":"queued","reason":"later"}""");

        ReplyRequest Reply(string? text, params string[] attachments) => new()
        {
            Id = SessionId, Request = Request, Text = text,
            Attachments = attachments.Length == 0 ? null : [.. attachments.Select(a => new AttachmentRef { Id = a })],
        };
        var eleven = Enumerable.Range(0, 11).Select(i => $"00000000-0000-4000-8000-{i:000000000000}").ToArray();
        f.Record<ReplyRequest>(Client, VectorChecks.Replies)
            .Valid("text", Reply("Run the tests again, on Linux this time"))
            .Valid("attachments-and-no-words", Reply(null, FileA, FileB))
            .Valid("text-at-its-16-KiB-limit", Reply(Repeat('a', Limits.ReplyText)))
            .Valid("ten-attachments", Reply("see these", [.. eleven.Take(10)]))
            .Valid("a-slash-after-the-first-word", Reply("use the /tmp folder"))
            .Valid("a-no-break-space-then-words", Reply("\u00A0hello"))
            .Valid("a-joined-emoji-then-a-slash", Reply("\U0001F468\u200D\U0001F469\u200D\U0001F467 /tmp is full"))
            .Refused("empty", Reply(""))
            .Refused("only-spaces-and-controls", Reply(" \t\r\n\u0007 "))
            .Refused("over-16-KiB", Reply(Repeat('a', Limits.ReplyText + 1)))
            .Refused("a-slash-command", Reply("/clear"))
            .Refused("a-shell-command-after-spaces", Reply(" \t!rm -rf build"))
            .Refused("a-slash-command-after-a-no-break-space", Reply("\u00A0/clear"))
            .Refused("a-slash-command-after-a-zero-width-space", Reply("\u200B/clear"))
            .Refused("a-lone-slash-after-a-hangul-filler", Reply("\u3164/"))
            .Refused("a-shell-command-after-a-tag-character", Reply("\U000E0001!dir"))
            .Refused("only-invisible-characters", Reply("\u3164\u200B\uFE0F\u2800"))
            .Refused("a-slash-command-after-a-combining-grapheme-joiner", Reply("\u034F/clear"))
            .Refused("a-shell-command-after-an-invisible-operator", Reply("\u2065!dir"))
            .Refused("a-slash-command-after-a-language-tag", Reply("\U000E0000/clear"))
            .Refused("a-slash-command-after-a-variation-selector-supplement", Reply("\U000E0100/clear"))
            .Refused("a-slash-command-after-a-blank-braille-pattern", Reply("\u2800/clear"))
            .Refused("a-slash-command-after-a-combining-mark", Reply("\u0301/clear"))
            .Refused("a-shell-command-after-an-unassigned-code-point", Reply("\u0378!dir"))
            .Refused("eleven-attachments", Reply("see these", eleven))
            .Refused("an-attachment-that-is-not-a-uuid", Reply("see this", "photo.png"))
            .Refused("the-same-attachment-twice", Reply("see this", FileA, FileA.ToUpperInvariant()))
            .Refused("a-null-attachment", $$"""{"id":"{{SessionId}}","request":"{{Request}}","text":"x","attachments":[null]}""");
        f.Record<ReplyResult>(Host)
            .Valid("delivered", new ReplyResult(AnswerOutcome.Delivered))
            .Valid("busy", new ReplyResult(AnswerOutcome.Busy, "an answer to this session is being typed"))
            .Reads("an-unknown-outcome-reads-as-unconfirmed", """{"outcome":"typed"}""");
        // session.reply and session.answer with progress: true (sessions.progress).
        f.Record<DeliveryProgress>(Host)
            .Valid("received", new DeliveryProgress(DeliveryProgress.Received))
            .Valid("typed-into-a-working-session", new DeliveryProgress(DeliveryProgress.Typed, true))
            .Valid("typed-into-an-idle-session", new DeliveryProgress(DeliveryProgress.Typed, false));
        f.Record<DeliveryTimings>(Host)
            .Valid("delivered", new DeliveryTimings { Typed = 352, Recorded = 1190, Confirmed = 1361 })
            .Valid("typed-and-not-recorded", new DeliveryTimings { Typed = 352 });

        f.Record<StopRequest>(Client, VectorChecks.Stops)
            .Valid("first-press", new StopRequest { Id = SessionId, Request = Request })
            .Valid("second-press", new StopRequest { Id = SessionId, Request = Request, Confirm = "q2vU3dJ7n0x-AbC_9zYw1A" })
            .Valid("a-token-at-64-characters", new StopRequest { Id = SessionId, Request = Request, Confirm = Repeat('A', Stops.MaxConfirmLength) })
            .Refused("an-empty-session-id", new StopRequest { Id = "", Request = Request })
            .Refused("a-session-id-over-256-bytes", new StopRequest { Id = Repeat('a', 257), Request = Request })
            .Refused("an-empty-token", new StopRequest { Id = SessionId, Request = Request, Confirm = "" })
            .Refused("a-token-over-64-characters", new StopRequest { Id = SessionId, Request = Request, Confirm = Repeat('A', Stops.MaxConfirmLength + 1) })
            .Refused("a-token-with-other-characters", new StopRequest { Id = SessionId, Request = Request, Confirm = "q2vU3dJ7+n0x/AbC=" });
        f.Record<StopResult>(Host)
            .Valid("ended", new StopResult(StopOutcome.Ended))
            .Valid("ended-by-force", new StopResult(StopOutcome.Ended) { Forced = true })
            .Valid("confirm", new StopResult(StopOutcome.Confirm, "this session is still working; the confirmation lapses in 10 s") { Confirm = "q2vU3dJ7n0x-AbC_9zYw1A", ConfirmSeconds = 10 })
            .Valid("refused", new StopResult(StopOutcome.Refused, "the session is elevated"))
            .Reads("an-unknown-outcome-reads-as-failed", """{"outcome":"paused","reason":"later"}""");
    }

    // ---- attachments ----

    // A file of size zero bytes, with its real SHA-256 where it can go in the channel, so a client
    // can prepare that very file and must send this very start.
    static AttachmentStart Start(long size = 1024, string? type = "image/png") => new()
    {
        Attachment = FileA, Name = "screenshot.png", Type = type, Size = size,
        Sha256 = size is > 0 and <= AttachmentLimits.Bytes ? Attachments.Base64Url(SHA256.HashData(new byte[size])) : Sha,
    };

    static void AttachmentRecords(VectorFileBuilder f)
    {
        var blob = new BlobRef { Id = "b-2026.10.28_7f3a", Key = BlobKey };
        const string direct = "a-direct-channel";
        f.Context(direct, new { inChannel = AttachmentLimits.DirectBytes });
        f.Record<AttachmentStart>(Client, VectorChecks.AttachmentStart)
            .Valid("in-the-channel", Start())
            .Valid("no-type", Start(type: null))
            .Valid("at-the-channels-256-KiB-limit", Start(AttachmentLimits.Bytes))
            .Valid("through-the-blob-store", Start(10L * 1024 * 1024) with { Blob = blob })
            .Valid("at-the-blob-stores-100-MiB-limit", Start(AttachmentLimits.BlobBytes) with { Blob = blob })
            .Valid("a-large-file-on-a-direct-channel", Start(AttachmentLimits.DirectBytes), direct)
            .Valid("a-name-at-255-bytes", Start() with { Name = Repeat('n', AttachmentLimits.Name) })
            .Refused("an-attachment-that-is-not-a-uuid", Start() with { Attachment = "screenshot" })
            .Refused("a-blank-name", Start() with { Name = " " })
            .Refused("a-name-over-255-bytes", Start() with { Name = Repeat('n', AttachmentLimits.Name + 1) })
            .Refused("a-type-over-127-bytes", Start(type: Repeat('t', AttachmentLimits.Type + 1)))
            .Refused("empty", Start(0))
            .Refused("over-256-KiB-in-the-channel", Start(AttachmentLimits.Bytes + 1))
            .Refused("over-100-MiB-on-a-direct-channel", Start(AttachmentLimits.DirectBytes + 1), direct)
            .Refused("over-100-MiB-through-the-blob-store", Start(AttachmentLimits.BlobBytes + 1) with { Blob = blob })
            .Refused("a-blob-id-with-a-slash", Start(1024) with { Blob = blob with { Id = "../other" } })
            .Refused("a-blob-id-over-512-characters", Start(1024) with { Blob = blob with { Id = Repeat('b', AttachmentLimits.BlobId + 1) } })
            .Refused("a-blob-key-that-is-not-32-bytes", Start(1024) with { Blob = blob with { Key = BlobKey[..42] } })
            .Refused("a-sha256-with-padding", Start() with { Sha256 = Sha + "=" });
        var data = Convert.ToBase64String("hello"u8);
        // The file each chunk comes from: a full chunk is the first of a longer file, so a client
        // that would send more than the host takes in one sends more than the vector.
        const string whole = "the-whole-file-in-one-chunk", longer = "a-file-of-two-full-chunks-and-more";
        f.Context(whole, new { fileSize = 5 }).Context(longer, new { fileSize = 2 * AttachmentLimits.Chunk + 5 });
        f.Record<AttachmentChunk>(Client, VectorChecks.AttachmentChunk)
            .Valid("the-first-chunk", new AttachmentChunk { Attachment = FileA, Offset = 0, Data = data }, whole)
            .Valid("a-full-11-KiB-chunk", new AttachmentChunk { Attachment = FileA, Offset = 0, Data = Convert.ToBase64String(new byte[AttachmentLimits.Chunk]) }, longer)
            .Refused("an-attachment-that-is-not-a-uuid", new AttachmentChunk { Attachment = "x", Offset = 0, Data = data })
            .Refused("a-negative-offset", new AttachmentChunk { Attachment = FileA, Offset = -1, Data = data })
            .Refused("an-offset-past-100-MiB", new AttachmentChunk { Attachment = FileA, Offset = AttachmentLimits.DirectBytes, Data = data })
            .Refused("no-data", new AttachmentChunk { Attachment = FileA, Offset = 0, Data = "" })
            .Refused("data-that-is-not-base64", new AttachmentChunk { Attachment = FileA, Offset = 0, Data = "not base64!" })
            .Refused("over-11-KiB", new AttachmentChunk { Attachment = FileA, Offset = 0, Data = Convert.ToBase64String(new byte[AttachmentLimits.Chunk + 1]) });
        f.Record<AttachmentDiscard>(Client).Valid("one", new AttachmentDiscard { Attachment = FileA });
        f.Record<AttachmentState>(Host)
            .Valid("part-way", new AttachmentState(11264, false))
            .Valid("complete", new AttachmentState(20480, true));
        f.Record<AttachmentDiscarded>(Host)
            .Valid("discarded", new AttachmentDiscarded(true))
            .Valid("nothing-to-discard", new AttachmentDiscarded(false));
        f.Record<AttachmentRef>(Client).Valid("one", new AttachmentRef { Id = FileA });
        f.Record<BlobRef>(Client).Valid("one", blob);
    }

    // ---- notifications ----

    static void Push(VectorFileBuilder f)
    {
        // A real P-256 point (the signed-command vectors' signer) and a 16-byte secret, as the host
        // checks them (PushNotifier.Problem; PushVectorTests in AgentBridge.Host.Tests).
        var keys = new PushKeys("BKzKtn9kLPn5w1wkF1-3gOdtAPCieCpa8m26esY0ZHt_c9CCDcF-wjeuJwqZOyt0ANZiKE0bkhyDwbg5E2FjE48", Attachments.Base64Url([.. Enumerable.Range(16, 16).Select(i => (byte)i)]));
        f.Record<PushSubscribeRequest>(Client)
            .Valid("full", new PushSubscribeRequest { Endpoint = "https://web.push.apple.com/QGx0c0", Keys = keys, App = "https://danswett.github.io/agent-bridge/", Device = "iPhone · Agent Bridge" })
            .Valid("no-device-name", new PushSubscribeRequest { Endpoint = "https://fcm.googleapis.com/fcm/send/abc", Keys = keys, App = "https://danswett.github.io/agent-bridge/" });
        f.Record<PushKeys>(Client).Valid("keys", keys);
        f.Record<PushSubscribeResult>(Host).Valid("subscribed", new PushSubscribeResult(true, 2));
        f.Record<PushUnsubscribeRequest>(Client)
            .Valid("this-device", new PushUnsubscribeRequest())
            .Valid("an-endpoint", new PushUnsubscribeRequest("https://web.push.apple.com/QGx0c0"));
        f.Record<PushUnsubscribeResult>(Host).Valid("removed", new PushUnsubscribeResult(true));
        var device = new PushDevice("3f2a", "iPhone · Agent Bridge", "web.push.apple.com", T0, true);
        f.Record<PushDevice>(Host).Valid("this-one", device);
        f.Record<PushListResult>(Host)
            .Valid("two", new PushListResult([device, device with { Device = "9c1d", Name = "Windows · Agent Bridge", Service = "wns2-par02p.notify.windows.com", Current = false }]))
            .Valid("none", new PushListResult([]));
        var data = new PushNoticeData { Kind = NoticeKind.Question, Machine = MachineId, Session = SessionId, Question = "call_q1", At = T0 };
        f.Record<PushNoticeData>(Host)
            .Valid("question", data)
            .Valid("finished", data with { Kind = NoticeKind.Finished, Question = null })
            .Valid("sign-in", SignInData)
            .Reads("an-unknown-kind-reads-as-a-question", $$"""{"v":1,"kind":"celebrate","machine":"{{MachineId}}","session":"{{SessionId}}","at":"2026-10-28T04:22:43.182Z"}""");
        var notification = new PushNotification
        {
            Title = "DEV-VM1 · Protocol vectors", Body = "Which spike next, and why?", Tag = PushNotice.Tag(MachineId, SessionId), Data = data,
            Navigate = PushNotice.Navigate("https://danswett.github.io/agent-bridge/", MachineId, SessionId, "call_q1"),
        };
        f.Record<PushNotification>(Host)
            .Valid("question", notification)
            .Valid("resolved", notification with { Silent = true, Body = "Answered", Data = data with { Kind = NoticeKind.Resolved } })
            .Valid("sign-in", new PushNotification
            {
                Title = "Copilot on DEV-VM1 needs sign-in", Body = "3 sessions affected", Tag = PushNotice.Tag(MachineId, PushNotice.SignInKey(AgentKind.Copilot, null)), Data = SignInData,
                Navigate = PushNotice.SignInNavigate("https://danswett.github.io/agent-bridge/", MachineId, AgentKind.Copilot),
            });
        f.Record<PushMessage>(Host).Valid("question", new PushMessage { Notification = notification });
    }

    static PushNoticeData SignInData => new() { Kind = NoticeKind.SignIn, Machine = MachineId, Agent = AgentKind.Copilot, Sessions = 3, At = T0 };

    // ---- launching ----

    static readonly SignedCommand Signed = new() { Body = "eyJ2IjoxfQ", By = "BHf3a2", Mac = "_PJ9LEydaL4_cLc4dUQ-wkKfhFj1DfQi-9K7Bl_0fxs" };
    static readonly LaunchTransfer Bring = new() { From = OtherMachine, Session = SessionId, Command = Signed };

    static readonly TrustQuestion Trust = new()
    {
        Id = "trust-1", Agent = "codex", Kind = AgentKind.Codex, Label = "Codex", Workspace = "agent-bridge", Folder = @"C:\src\agent-bridge\pwa", Root = @"C:\src\agent-bridge",
        RootNote = "Trusting applies to the whole repository", Warning = "Do you trust the files in this folder?", AskedAt = T0, Answerable = true,
    };

    static void Launch(VectorFileBuilder f)
    {
        LaunchRequest New() => new() { Request = Request, Agent = "copilot", Workspace = "agent-bridge" };
        var eleven = Enumerable.Range(0, 11).Select(i => new AttachmentRef { Id = $"00000000-0000-4000-8000-{i:000000000000}" }).ToList();
        f.Record<LaunchRequest>(Client, VectorChecks.Launch)
            .Valid("the-machines-defaults", new LaunchRequest { Request = Request })
            .Valid("every-setting", New() with { Profile = "work", Model = "claude-opus-5.5", Effort = "high", Context = "1m", Permissions = LaunchProtocol.AllowAll, Prompt = "Fix the flaky test", Attachments = [new AttachmentRef { Id = FileA }] })
            .Valid("the-agents-own-defaults", New() with { Model = "", Effort = "", Context = "" })
            .Valid("a-resume", new LaunchRequest { Request = Request, Agent = "claude", Resume = SessionId })
            .Valid("a-transfer", New() with { Transfer = Bring })
            .Valid("a-prompt-at-its-16-KiB-limit", New() with { Prompt = Repeat('a', LaunchProtocol.MaxPrompt) })
            .Valid("a-model-with-brackets-and-colons", New() with { Model = "gpt-5.6[1m]:latest" })
            .Refused("a-prompt-led-by-a-byte-order-mark", New() with { Prompt = "\uFEFF/clear" })
            .Refused("a-prompt-led-by-a-hangul-filler", New() with { Prompt = "\u3164/clear" })
            .Refused("a-shell-command-led-by-a-variation-selector", New() with { Prompt = "\uFE0F!dir" })
            .Refused("a-prompt-led-by-a-combining-grapheme-joiner", New() with { Prompt = "\u034F/clear" })
            .Refused("a-prompt-led-by-an-invisible-operator", New() with { Prompt = "\u2065!dir" })
            .Refused("a-prompt-led-by-tag-and-variation-selectors", New() with { Prompt = "\U000E0000\U000E0100/clear" })
            .Refused("a-prompt-led-by-a-blank-braille-pattern", New() with { Prompt = "\u2800/clear" })
            .Refused("a-prompt-led-by-a-combining-mark", New() with { Prompt = "\u0301/clear" })
            .Refused("a-prompt-led-by-an-unassigned-code-point", New() with { Prompt = "\u0378!dir" })
            .Refused("an-unknown-agent", New() with { Agent = "gemini" })
            .Refused("a-workspace-over-256-bytes", New() with { Workspace = Repeat('w', LaunchProtocol.MaxName + 1) })
            .Refused("a-profile-with-a-control-character", New() with { Profile = "work\nrm" })
            .Refused("a-model-with-a-space", New() with { Model = "gpt 5" })
            .Refused("a-model-led-by-a-dash", New() with { Model = "-rf" })
            .Refused("a-resume-and-a-workspace", New() with { Resume = SessionId })
            .Refused("an-empty-resume", new LaunchRequest { Request = Request, Resume = "" })
            .Refused("a-transfer-and-a-resume", new LaunchRequest { Request = Request, Resume = SessionId, Transfer = Bring })
            .Refused("a-transfer-without-a-workspace", new LaunchRequest { Request = Request, Transfer = Bring })
            .Refused("a-transfer-from-something-not-a-device-id", New() with { Transfer = Bring with { From = "DEV-VM2" } })
            .Refused("a-transfer-of-something-not-a-uuid", New() with { Transfer = Bring with { Session = "latest" } })
            .Refused("a-transfer-without-a-command", New() with { Transfer = Bring with { Command = null } })
            .Refused("a-transfer-with-a-malformed-command", New() with { Transfer = Bring with { Command = Signed with { Mac = "not a mac!" } } })
            .Refused("an-empty-workspace", New() with { Workspace = "" })
            .Refused("unknown-permissions", New() with { Permissions = "yolo" })
            .Refused("eleven-attachments", New() with { Attachments = eleven })
            .Refused("an-attachment-that-is-not-a-uuid", New() with { Attachments = [new AttachmentRef { Id = "photo.png" }] })
            .Refused("the-same-attachment-twice", New() with { Attachments = [new AttachmentRef { Id = FileA }, new AttachmentRef { Id = FileA.ToUpperInvariant() }] })
            .Refused("a-prompt-over-16-KiB", New() with { Prompt = Repeat('a', LaunchProtocol.MaxPrompt + 1) })
            .Refused("a-slash-command-prompt", New() with { Prompt = "/clear" })
            .Refused("a-shell-command-prompt-after-a-line-break", New() with { Prompt = "\n!ls" });
        var launched = new LaunchedWith
        {
            Agent = "copilot", Workspace = "agent-bridge", Cwd = @"C:\src\agent-bridge.worktrees\launch-1", Worktree = true, Profile = "work",
            Model = "claude-opus-5.5", Effort = "high", Context = "1m", Permissions = LaunchProtocol.Ask,
        };
        var transferred = new LaunchedTransfer { From = OtherMachine, Session = SessionId, Bytes = 1_048_576 };
        f.Record<LaunchedTransfer>(Host).Valid("one", transferred);
        f.Record<LaunchedWith>(Host)
            .Valid("a-new-session", launched)
            .Valid("a-transfer", new LaunchedWith { Agent = "claude", Workspace = "agent-bridge", Resume = "c0ffee00-0000-4000-8000-000000000002", Permissions = LaunchProtocol.Ask, Transfer = transferred });
        f.Record<LaunchResult>(Host)
            .Valid("started", new LaunchResult(LaunchOutcome.Started) { Session = SessionId, Launched = launched })
            .Valid("busy-on-a-trust-question", new LaunchResult(LaunchOutcome.Busy, "the agent is asking whether to trust its folder") { Trust = Trust })
            .Valid("refused", new LaunchResult(LaunchOutcome.Refused, "unknown agent 'gemini'"))
            .Reads("an-unknown-outcome-reads-as-unconfirmed", """{"outcome":"queued"}""");
        var tuning = new LaunchTuning { Model = "claude-opus-5.5", Effort = "high", Context = "1m", Profile = "work" };
        f.Record<LaunchTuning>(Host).Valid("full", tuning).Valid("empty", new LaunchTuning());
        var copilot = new LaunchAgent
        {
            Id = "copilot", Label = "Copilot CLI", Kind = AgentKind.Copilot, Models = ["claude-opus-5.5", "gpt-5.6-sol"], Efforts = ["low", "high"], Contexts = ["1m"],
            Profiles = ["work"], Defaults = tuning, Attachments = true,
        };
        var codex = new LaunchAgent { Id = "codex", Label = "Codex", Kind = AgentKind.Codex, NeedsPrompt = true };
        f.Record<LaunchAgent>(Host).Valid("full", copilot).Valid("minimal", codex);
        var workspace = new LaunchWorkspace { Label = "agent-bridge", Path = @"C:\src\agent-bridge", Isolate = true };
        f.Record<LaunchWorkspace>(Host).Valid("isolated", workspace).Valid("plain", new LaunchWorkspace { Label = "notes", Path = "/home/dev/notes" });
        var resumable = new ResumableSession { Id = SessionId, Agent = AgentKind.Copilot, Launcher = "agency", Title = "Protocol vectors", Cwd = @"C:\src\agent-bridge", Workspace = "agent-bridge", UpdatedAt = T0 };
        f.Record<ResumableSession>(Host).Valid("full", resumable).Valid("minimal", new ResumableSession { Id = "s-2", Agent = AgentKind.Codex, Launcher = "codex", Title = "s-2" });
        var defaults = new LaunchDefaults { Agent = "copilot", Workspace = "agent-bridge", Permissions = LaunchProtocol.Ask };
        f.Record<LaunchDefaults>(Host).Valid("full", defaults).Valid("permissions-only", new LaunchDefaults { Permissions = LaunchProtocol.AllowAll });
        f.Record<LaunchOptions>(Host)
            .Valid("full", new LaunchOptions { Agents = [copilot, codex], Workspaces = [workspace], Resumable = [resumable], Defaults = defaults, Busy = "a launch is starting", Trust = [Trust] })
            .Valid("nothing-offered", new LaunchOptions { Defaults = new LaunchDefaults { Permissions = LaunchProtocol.Ask } });

        f.Record<TrustQuestion>(Host)
            .Valid("full", Trust)
            .Valid("answered-at-the-machine", new TrustQuestion { Id = "trust-2", Agent = "copilot", Kind = AgentKind.Copilot, Label = "Copilot CLI", Folder = "/home/dev/x", AskedAt = T0, Answerable = false, AtMachine = "launch.trust is off on this machine" });
        f.Record<TrustAnswerRequest>(Client)
            .Valid("trust", new TrustAnswerRequest { Question = "trust-1", Choice = TrustProtocol.Trust, Command = Signed })
            .Valid("quit", new TrustAnswerRequest { Question = "trust-1", Choice = TrustProtocol.Quit, Command = Signed });
        f.Record<TrustAnswerResult>(Host)
            .Valid("started", new TrustAnswerResult(TrustAnswerOutcome.Started) { Session = SessionId })
            .Valid("declined", new TrustAnswerResult(TrustAnswerOutcome.Declined))
            .Reads("an-unknown-outcome-reads-as-unconfirmed", """{"outcome":"pondering"}""");
    }

    // ---- transfer ----

    static void Transfer(VectorFileBuilder f)
    {
        f.Record<SignedCommand>(Client, VectorChecks.Transfer)
            .Valid("well-formed", Signed)
            .Valid("a-body-at-2048-characters", Signed with { Body = Repeat('b', TransferProtocol.MaxBody) })
            .Refused("standard-base64", Signed with { Body = "eyJ2IjoxfQ+/" })
            .Refused("an-empty-mac", Signed with { Mac = "" })
            .Refused("a-body-over-2048-characters", Signed with { Body = Repeat('b', TransferProtocol.MaxBody + 1) })
            .Refused("a-key-over-96-characters", Signed with { By = Repeat('k', TransferProtocol.MaxKey + 1) })
            .Refused("a-mac-over-64-characters", Signed with { Mac = Repeat('m', TransferProtocol.MaxMac + 1) });
        f.Record<LaunchTransfer>(Client, VectorChecks.Transfer)
            .Valid("signed", Bring)
            .Refused("from-a-name", Bring with { From = "DEV-VM2" })
            .Refused("from-an-upper-case-device-id", Bring with { From = OtherMachine.ToUpperInvariant() })
            .Refused("a-session-that-is-not-a-uuid", Bring with { Session = "latest" })
            .Refused("no-command", Bring with { Command = null });
        var principal = new CommandPrincipal { Kind = "user", Device = "8e7d6c5b4a39281706f5e4d3c2b1a090" };
        var target = new CommandTarget { Machine = MachineId, Session = SessionId };
        f.Record<CommandPrincipal>(Client).Valid("a-person", principal);
        f.Record<CommandTarget>(Client).Valid("a-session", target).Valid("a-machine", new CommandTarget { Machine = MachineId });
        f.Record<CommandBody>(Client)
            .Valid("a-transfer", new CommandBody { V = 1, Action = TransferProtocol.Action, Request = Request, Principal = principal, Target = target, To = OtherMachine, IssuedAt = 1_793_300_000, ExpiresAt = 1_793_300_300 })
            .Valid("a-trust-answer", new CommandBody { V = 1, Action = TrustProtocol.Action(TrustProtocol.Trust), Request = Request, Principal = principal, Target = target with { Session = "trust-1" }, To = MachineId, IssuedAt = 1_793_300_000, ExpiresAt = 1_793_300_300 });
        f.Record<TransferRequest>(HostToHost).Valid("signed", new TransferRequest { Command = Signed });
        var result = new TransferResult
        {
            Blob = new BlobRef { Id = "b-2026.10.28_9e1c", Key = BlobKey }, Size = 1_048_576, Sha256 = Sha, Agent = AgentKind.Copilot, Launcher = "agency",
            Title = "Protocol vectors", Files = 12, AgentVersion = "1.0.40",
        };
        const string max = "a-machine-taking-25-MiB";
        f.Context(max, new { maxBytes = TransferProtocol.DefaultMaxBytes });
        f.Record<TransferResult>(HostToHost, VectorChecks.TransferResult)
            .Valid("a-bundle", result, max)
            .Valid("at-the-25-MiB-default", result with { Size = TransferProtocol.DefaultMaxBytes }, max)
            .Refused("empty", result with { Size = 0 }, max)
            .Refused("no-blob-id", result with { Blob = result.Blob with { Id = "" } }, max)
            .Refused("over-what-the-machine-takes", result with { Size = TransferProtocol.DefaultMaxBytes + 1 }, max)
            .Refused("a-malformed-sha256", result with { Sha256 = "abc" }, max)
            .Refused("no-files", result with { Files = 0 }, max)
            .Refused("over-20000-files", result with { Files = TransferLimits.MaxFiles + 1 }, max)
            .Refused("an-unknown-launcher", result with { Launcher = "gemini" }, max)
            .Refused("a-title-over-480-bytes", result with { Title = Repeat('t', LaunchProtocol.MaxLabel * 4 + 1) }, max);
    }

    // ---- updates ----

    static void Update(VectorFileBuilder f)
    {
        var last = new UpdateResult { Outcome = UpdateOutcome.Updated, From = "0.1.199", To = "0.1.200", Request = Request, At = T0 };
        f.Record<UpdateResult>(Host)
            .Valid("updated", last)
            .Valid("refused", new UpdateResult { Outcome = UpdateOutcome.Refused, Reason = "a session is busy", At = T0 })
            .Reads("an-unknown-outcome-reads-as-failed", """{"outcome":"rolledBack","at":"2026-10-28T04:22:43.182Z"}""");
        var state = new UpdateState
        {
            Current = "0.1.200", Installer = true, Available = "0.1.201", Notes = "Fixes the flaky reconnect.", CheckedAt = T0, Phase = UpdatePhase.Downloading, Progress = 40,
            Request = Request, Automatic = true, Last = last,
        };
        f.Record<UpdateState>(Host)
            .Valid("downloading", state)
            .Valid("waiting", state with { Phase = UpdatePhase.Waiting, Progress = null, Waiting = "a session is busy" })
            .Valid("no-app", new UpdateState { Unavailable = "the Windows app is not running" })
            .Reads("an-unknown-phase-reads-as-idle", """{"current":"0.1.200","installer":true,"phase":"verifying","automatic":false}""");
        f.Record<UpdateCommand>(Client).Valid("a-press", new UpdateCommand(Request));
        f.Record<UpdateCommandResult>(Host)
            .Valid("accepted", new UpdateCommandResult(UpdateAck.Accepted) { State = state with { Phase = UpdatePhase.Checking, Progress = null } })
            .Valid("refused", new UpdateCommandResult(UpdateAck.Refused, "the machine is read-only"))
            .Reads("an-unknown-outcome-reads-as-refused", """{"outcome":"maybe"}""");
    }

    // ---- usage ----

    static void Usage(VectorFileBuilder f)
    {
        var window = new UsageWindow { Key = "premium", Label = "Premium requests", Percent = 42.5, Used = 127.5, Limit = 300, Unit = "requests", StartsAt = T0, ResetsAt = T0.AddDays(30) };
        f.Record<UsageWindow>(Host)
            .Valid("full", window)
            .Valid("percent-only", new UsageWindow { Key = "5h", Label = "5-hour", Percent = 3 });
        var reader = new UsageReader(OtherMachine, "DEV-VM2");
        f.Record<UsageReader>(Host).Valid("another-machine", reader);
        var reading = new AgentUsage { Agent = AgentKind.Copilot, Label = "Copilot", Account = "octocat", Plan = "Pro+", Source = UsageSource.Api, Windows = [window], MeasuredAt = T0 };
        f.Record<AgentUsage>(Host)
            .Valid("a-reading", reading)
            .Valid("a-further-account-read-elsewhere", reading with { Account = "hubot", Slot = 2, ReadBy = reader })
            .Valid("a-failure", new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "a1b2c3d4", Source = UsageSource.None, Error = "the usage endpoint answered 401", MeasuredAt = T0 })
            .Reads("an-unknown-agent-and-source", """{"agent":"gemini","label":"Gemini","source":"scrape","windows":[],"measuredAt":"2026-10-28T04:22:43.182Z"}""");
        f.Record<UsageRequest>(Client)
            .Valid("own", new UsageRequest())
            .Valid("shared", new UsageRequest { Shared = true });
        f.Record<UsageReport>(Host)
            .Valid("two-agents", new UsageReport([reading, new AgentUsage { Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.Transcript, Windows = [new UsageWindow { Key = "weekly", Label = "Weekly", Percent = 90 }], MeasuredAt = T0 }]))
            .Valid("none", UsageReport.Empty);
    }
}
