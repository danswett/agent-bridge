using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// A window as heavy as a real one, for the self-test's live-resize pass (#765): sixteen more
// sessions, one of them with a conversation of over 200 items, a full tool history and a strip of
// delegates. Added only when asked, after the self-test's other checks have counted the examples.
public sealed partial class DemoHost
{
    public const string HeavyId = "demo-heavy";
    public const int HeavySessions = 16;
    public const int HeavyDelegates = 20;
    // The blocks of the heavy session's response that the conversation draws (a list's items each).
    public const int HeavyBlocks = 210;

    // Added to the sessions already listed, and sent to every window listening, at once.
    public async Task AddHeavyAsync()
    {
        var add = Heavy(DateTimeOffset.UtcNow);
        IProtocolChannel[] to;
        lock (gate)
        {
            foreach (var s in add) { sessions[s.Id] = s; }
            revision++;
            to = [.. subscribers.Where(s => s.IsOpen)];
        }
        foreach (var ch in to)
        {
            foreach (var s in add)
            {
                try { await ch.SendEventAsync(Events.SessionUpsert, s.WithoutHistory()); }
                catch (IOException) { break; }
            }
        }
    }

    // Older than the examples, so they sort below them and the list's top is as it was.
    public static IReadOnlyList<Session> Heavy(DateTimeOffset now)
    {
        string[] titles = ["Tidy the release notes", "Bump Markdig", "Retry the relay handshake", "Port the toast plan", "Trim the usage panel", "Read the crash dump", "Check the Arm64 build", "Rename the profile folder", "Speed up the session list", "Explain the witness timer", "Draft the 0.3 changelog", "Find the flaky test", "Review the installer", "Sort the launch options", "Fix the tray icon's tooltip"];
        AgentKind[] agents = [AgentKind.Copilot, AgentKind.Claude, AgentKind.Codex];
        var list = new List<Session>();
        for (var i = 0; i < HeavySessions - 1; i++)
        {
            var at = now.AddMinutes(-30 - i * 7);
            list.Add(new Session
            {
                Id = $"demo-light-{i + 1}",
                Agent = agents[i % agents.Length],
                Title = titles[i],
                Repository = "danswett/agent-bridge",
                Branch = $"demo/branch-{i + 1}",
                Model = i % 2 == 0 ? "claude-opus-5.5" : "gpt-5.6-luna",
                // Three under way, their glyphs moving as a real window's do.
                Status = i % 5 == 0 ? SessionStatus.Working : SessionStatus.Idle,
                StatusSince = at,
                StartedAt = at.AddMinutes(-20),
                UpdatedAt = at,
                Activity = new Activity { Response = "Done: the change is on its branch, and the suite passes.", TurnCount = 3 + i },
            });
        }
        list.Add(HeavySession(now.AddMinutes(-25)));
        return list;
    }

    static Session HeavySession(DateTimeOffset at) => new()
    {
        Id = HeavyId,
        Agent = AgentKind.Copilot,
        Title = "Make the live resize smooth",
        Repository = "danswett/agent-bridge",
        Branch = "perf/765-smooth-resize",
        Model = "claude-opus-5.5",
        Status = SessionStatus.Idle,
        StatusSince = at,
        StartedAt = at.AddMinutes(-60),
        UpdatedAt = at,
        Activity = new Activity
        {
            Response = HeavyResponse(),
            Reasoning = "Measure first: the drag's frames, then the work in each.",
            UserMessage = "Find what makes resizing the window jerky, and fix it.",
            TurnCount = 42,
            Background = Background.Of(6, 0),
            History = HeavyHistory(at),
        },
        Subagents = HeavySubagents(at),
    };

    // HeavyBlocks short blocks, as a long turn's response is: paragraphs, some with a link, headings, code and lists.
    static string HeavyResponse()
    {
        string[] said =
        [
            "Read `SessionsPage.Dividers.cs`: the panes are sized as the page is.",
            "The suite passes on HEAD.",
            "Two of the failures were the same timeout, so I fixed the timer once and reran both.",
            "**Next:** the inspector's width, kept on the device across restarts.",
            "No change to the protocol; the host keeps sending what it sent.",
            "Checked reduced motion as well: nothing moves, and the state still changes at once.",
            "Left as it was.",
            "The list keeps its width at two panes while the session keeps its narrowest.",
        ];
        var blocks = new List<string>();
        var items = 0;
        for (var i = 0; items < HeavyBlocks; i++)
        {
            if (i % 25 == 0) { blocks.Add($"## Step {i / 25 + 1}"); items++; }
            // Two linked paragraphs in a row, so Tab goes from a link to a link in the next block.
            else if (i % 25 is 12 or 13) { blocks.Add($"Case {i} is in [the layout notes](https://github.com/danswett/agent-bridge/blob/main/docs/design/ui.md)."); items++; }
            else if (i % 11 == 5) { blocks.Add($"```\ndotnet test --filter Case{i}\n```"); items++; }
            else if (i % 17 == 8) { blocks.Add($"- the list's divider\n- the inspector's divider\n- the window's edge, case {i}"); items += 3; }
            else { blocks.Add(said[i % said.Length]); items++; }
        }
        // Said once, so a screen reader that finds the last block by its name finds that block.
        blocks.Add("That is the whole turn: every pane resized, every test run, nothing left open.");
        return string.Join("\n\n", blocks);
    }

    static IReadOnlyList<ToolCall> HeavyHistory(DateTimeOffset at)
    {
        string[] names = ["view", "edit", "powershell", "grep", "glob"];
        string[] summaries =
        [
            "src/AgentBridge.App/SessionsPage.xaml.cs",
            "src/AgentBridge.App/MainWindow.xaml.cs",
            "dotnet test tests/AgentBridge.App.Tests --filter FullyQualifiedName~PaneWidths -c Release --nologo",
            "SizeChanged in src/AgentBridge.App",
            "src/**/*.xaml",
        ];
        return [.. Enumerable.Range(0, Limits.History).Select(i => new ToolCall
        {
            Id = $"heavy-t{i + 1}",
            Name = names[i % names.Length],
            Summary = summaries[i % summaries.Length],
            Status = i % 9 == 4 ? ToolStatus.Failed : ToolStatus.Done,
            StartedAt = at.AddMinutes(-50 + i),
            EndedAt = at.AddMinutes(-50 + i).AddSeconds(20),
        })];
    }

    // Started minutes apart, so each start is its own line in the conversation; the running first.
    static IReadOnlyList<Subagent> HeavySubagents(DateTimeOffset at) =>
    [
        .. Enumerable.Range(0, HeavyDelegates).Select(i =>
        {
            var started = at.AddMinutes(-58 + i * 2.5);
            var running = i >= HeavyDelegates - 6;
            return new Subagent
            {
                Id = $"demo-heavy-agent-{i + 1}",
                Kind = running ? SubagentKind.Background : SubagentKind.Task,
                Name = $"Measure pass {i + 1}",
                Task = "Measure the frames of a live resize across the three layouts and report the slowest.",
                Model = i % 2 == 0 ? "claude-haiku-5.5" : "gpt-5.6-luna",
                Status = running ? SubagentStatus.Working : SubagentStatus.Ended,
                Outcome = running ? null : i % 4 == 3 ? SubagentOutcome.Failed : SubagentOutcome.Done,
                StartedAt = started,
                EndedAt = running ? null : started.AddMinutes(2),
                Line = running ? "Sweeping the window's width, 8 px a frame." : "p95 under a frame; the longest at the layout switch.",
            };
        }).OrderBy(a => a.Status == SubagentStatus.Working ? 0 : 1).ThenByDescending(a => a.EndedAt ?? a.StartedAt),
    ];
}
