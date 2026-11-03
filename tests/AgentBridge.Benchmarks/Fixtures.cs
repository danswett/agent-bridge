using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Benchmarks;

// The shapes of session the benchmarks time, by the sizes design.md §5.9 measured over 449 real
// sessions: an upsert (the list form, without tool history) is 2.7 KB typically and 7.3 KB at
// p99, and none may pass SessionBudget's 40 KiB.
public enum Shape
{
    // A Copilot session at work: a tool running, a reply, its reasoning and the prompt.
    Typical,
    // The same asking a question (a form of three fields), with a longer reply.
    P99,
    // Everything at its limit (a 16 KiB reply, sub-agents with their tasks), so SessionBudget.Fit
    // has to trim it: the most a host ever sends.
    Oversized,
}

// Everything here is deterministic, so every run times the same bytes.
public static class Fixtures
{
    const int TypicalBytes = 2700;
    const int P99Bytes = 7300;

    // About 21 MB at about 12 KB a turn: an ordinary Copilot events.jsonl.
    public const int LargeTranscriptTurns = 1800;

    static readonly DateTimeOffset T0 = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    static readonly string[] Words =
        ("the host reads each transcript once and keeps only what changed since its last pass so an idle session costs " +
         "nothing while a busy one sends at most four upserts a second through the relay channel as noise frames each " +
         "tool call shell command test suite build passed failed retried worktree branch review comment approval prompt " +
         "question answer delegate background agent").Split(' ');

    // Markdown as agents write it: paragraphs with inline code, lists and fenced blocks. ASCII,
    // so its length in characters is its length in bytes.
    public static string Prose(int bytes, int seed = 1)
    {
        var random = new Random(seed);
        var sb = new StringBuilder(bytes + 256);
        for (var block = 0; sb.Length < bytes; block++)
        {
            if (block % 7 == 6)
            {
                sb.Append("```powershell\n");
                for (var i = 0; i < 4; i++) { sb.Append("dotnet test tests/AgentBridge.Host.Core.Tests -c Release --filter \"Category!=Process\"\n"); }
                sb.Append("```\n\n");
            }
            else if (block % 4 == 3)
            {
                for (var i = 0; i < 3; i++) { sb.Append("- ").Append(Sentence(random, 12)).Append('\n'); }
                sb.Append('\n');
            }
            else { sb.Append(Sentence(random, 40)).Append(" See `SessionBudget.Fit` for the rest. ").Append(Sentence(random, 20)).Append("\n\n"); }
        }
        return sb.ToString(0, bytes);
    }

    static string Sentence(Random random, int words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            var w = Words[random.Next(Words.Length)];
            sb.Append(i == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w).Append(i == words - 1 ? '.' : ' ');
        }
        return sb.ToString();
    }

    // The session as the host holds it before SessionBudget.Fit, with its tool history.
    public static Session Full(Shape shape) => shape switch
    {
        Shape.Typical => WithHistory(Sized(n => Build(n, question: false, subagents: 0), TypicalBytes), 20),
        Shape.P99 => WithHistory(Sized(n => Build(n, question: true, subagents: 0), P99Bytes), Limits.History),
        _ => Oversized(),
    };

    static Session WithHistory(Session s, int count) => s with { Activity = s.Activity with { History = History(count) } };

    // The session.upsert a client receives: the list form, within the budget.
    public static Session Upsert(Shape shape) => SessionBudget.Fit(Full(shape).WithoutHistory());

    // The largest response that keeps the list form within target bytes.
    static Session Sized(Func<int, Session> build, int target)
    {
        int lo = 0, hi = target;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ProtocolJson.ByteCount(build(mid)) <= target) { lo = mid; } else { hi = mid - 1; }
        }
        return build(lo);
    }

    static Session Build(int responseBytes, bool question, int subagents) => new()
    {
        Id = "02d9e5d5-6b35-49bd-b083-39572fbff648",
        Agent = AgentKind.Copilot,
        Title = "Quality P2: performance, hook latency and micro-benchmarks",
        Cwd = @"C:\Users\someone\repos\wt\agent-bridge-q-perf",
        Repository = "danswett/agent-bridge",
        Branch = "quality/perf",
        Model = "claude-opus-5.5",
        Status = question ? SessionStatus.Waiting : SessionStatus.Working,
        StatusSince = T0.AddMinutes(41),
        StartedAt = T0,
        UpdatedAt = T0.AddMinutes(42),
        ProcessId = 39844,
        Question = question ? Question() : null,
        Subagents = subagents > 0 ? [.. Enumerable.Range(0, subagents).Select(Subagent)] : null,
        Activity = new Activity
        {
            Response = Prose(responseBytes, seed: 2),
            Reasoning = Prose(300, seed: 3),
            CurrentTool = question ? null : Tool(99, ToolStatus.Running),
            UserMessage = Prose(200, seed: 4),
            TurnCount = 37,
            Compactions = 1,
            Background = AgentBridge.Protocol.Background.Of(1, 1),
        },
    };

    static Session Oversized()
    {
        var s = Build(0, question: true, subagents: Limits.Subagents);
        return s with
        {
            Activity = s.Activity with
            {
                Response = Prose(Limits.Response, seed: 5),
                Reasoning = Prose(Limits.Reasoning, seed: 6),
                UserMessage = Prose(Limits.UserMessage, seed: 7),
                History = History(Limits.History),
                Source = new ActivitySource { Response = "agent-3" },
                Own = new OwnActivity { Response = Prose(Limits.Response, seed: 8), At = T0.AddMinutes(40) },
            },
        };
    }

    static Question Question() => new()
    {
        Id = "toolu_013DTzyjWNxfVUARbPqTnLLu",
        Kind = QuestionKind.Form,
        Message = Prose(600, seed: 9),
        AskedAt = T0.AddMinutes(41),
        Fields =
        [
            new Field
            {
                Name = "plan", Title = "Today's plan", Type = FieldType.Choice, AllowOther = true, Required = true,
                Options = [new Option("go", "Go: protocol, read-only host, PWA cards in parallel (Recommended)"), new Option("change", "Change the plan (describe below)")],
                Default = JsonDocument.Parse("\"go\"").RootElement,
            },
            new Field { Name = "areas", Title = "Areas", Type = FieldType.Multi, Options = [new Option("host"), new Option("pwa"), new Option("relay")] },
            new Field { Name = "notes", Title = "Notes (optional)", Type = FieldType.Text },
        ],
    };

    static IReadOnlyList<ToolCall> History(int count) => [.. Enumerable.Range(0, count).Select(i => Tool(i, i % 9 == 8 ? ToolStatus.Failed : ToolStatus.Done))];

    static ToolCall Tool(int i, ToolStatus status) => new()
    {
        Id = $"toolu_01A7rUnSu1teQx9dWm3k{i:D4}",
        Name = i % 3 == 0 ? "view" : "powershell",
        Summary = i % 3 == 0 ? @"C:\Users\someone\repos\wt\agent-bridge-q-perf\src\AgentBridge.Host.Core\SessionTracker.cs" : "dotnet test AgentBridge.slnx -c Release --filter \"Category!=Process&Category!=Network\"",
        Status = status,
        StartedAt = T0.AddSeconds(10 * i),
        EndedAt = status == ToolStatus.Running ? null : T0.AddSeconds(10 * i + 4),
    };

    static Subagent Subagent(int i) => new()
    {
        Id = $"agent-{i}",
        Kind = SubagentKind.Background,
        Name = $"delegate-{i}",
        Task = Prose(Limits.SubagentTask, seed: 20 + i),
        Model = "claude-haiku-5.5",
        Status = SubagentStatus.Working,
        StartedAt = T0.AddMinutes(i),
        Line = Prose(Limits.SubagentLine, seed: 40 + i),
        CurrentTool = Tool(200 + i, ToolStatus.Running),
        Shells = 1,
    };

    // A Copilot events.jsonl: the session's start, then turns of a prompt, a tool call with its
    // output and a reply, about 12 KB a turn as real ones run (tool output is most of it).
    public static void WriteCopilotTranscript(Stream to, int turns)
    {
        Line(to, "session.start", new
        {
            sessionId = "66666666-6666-4666-8666-666666666666",
            version = 1,
            producer = "copilot-agent",
            copilotVersion = "1.0.105",
            startTime = Stamp(T0),
            selectedModel = "claude-opus-5.5",
            context = new { cwd = "/work/repo", gitRoot = "/work/repo", repository = "octo/repo", branch = "main" },
        }, 0, 0, T0);
        for (var t = 1; t <= turns; t++) { CopilotTurn(to, t); }
    }

    public static byte[] CopilotTurnBytes(int turn)
    {
        using var ms = new MemoryStream();
        CopilotTurn(ms, turn);
        return ms.ToArray();
    }

    static void CopilotTurn(Stream to, int t)
    {
        var at = T0.AddSeconds(30 * t);
        var call = $"toolu_{t:D8}";
        Line(to, "user.message", new { content = Prose(200, seed: t), transformedContent = Prose(200, seed: t), attachments = Array.Empty<object>(), interactionId = Id(t, 0) }, t, 1, at);
        Line(to, "assistant.turn_start", new { turnId = t.ToString(System.Globalization.CultureInfo.InvariantCulture), interactionId = Id(t, 0) }, t, 2, at.AddSeconds(1));
        Line(to, "assistant.message", new
        {
            messageId = Id(t, 100),
            content = "",
            toolRequests = new[] { new { toolCallId = call, name = "powershell", arguments = new { command = "dotnet test", description = "Run the tests" }, type = "function" } },
            reasoningText = Prose(400, seed: t + 1),
            model = "claude-opus-5.5",
            interactionId = Id(t, 0),
        }, t, 3, at.AddSeconds(2));
        Line(to, "tool.execution_start", new { toolCallId = call, toolName = "powershell", arguments = new { command = "dotnet test", description = "Run the tests" } }, t, 4, at.AddSeconds(3));
        var output = Prose(3500, seed: t + 2);
        Line(to, "tool.execution_complete", new { toolCallId = call, model = "claude-opus-5.5", interactionId = Id(t, 0), success = true, result = new { content = output, detailedContent = output } }, t, 5, at.AddSeconds(9));
        Line(to, "assistant.message", new { messageId = Id(t, 101), content = Prose(1500, seed: t + 3), toolRequests = Array.Empty<object>(), model = "claude-opus-5.5", interactionId = Id(t, 0) }, t, 6, at.AddSeconds(12));
        Line(to, "assistant.turn_end", new { turnId = t.ToString(System.Globalization.CultureInfo.InvariantCulture), interactionId = Id(t, 0) }, t, 7, at.AddSeconds(13));
    }

    static void Line(Stream to, string type, object data, int turn, int k, DateTimeOffset at)
    {
        to.Write(JsonSerializer.SerializeToUtf8Bytes(new { type, data, id = Id(turn, k), timestamp = Stamp(at), parentId = Id(turn, k - 1) }));
        to.WriteByte((byte)'\n');
    }

    static string Id(int turn, int k) => $"{turn:x8}-0000-4000-8000-{k:x12}";

    static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
