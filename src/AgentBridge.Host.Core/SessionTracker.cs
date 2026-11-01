using System.Text.Json;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Core.Transcripts;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core;

// One live session: its transcript reader, parser and accumulated state, and how it is
// turned into the contract's Session.
internal sealed class SessionTracker
{
    readonly SessionSourceOptions _options;
    readonly TimeProvider _time;
    readonly SessionState _state = new();
    readonly TranscriptParser _parser;
    TranscriptTail? _tail;
    Workspace? _workspace;
    (DateTime, long) _workspaceStamp;
    SessionStatus? _lastStatus;
    DateTimeOffset _lastSince;
    string? _lastShown;
    DateTimeOffset? _lastUpdatedAt;
    IReadOnlyList<Subagent>? _lastSubagents;
    DateTimeOffset _firstSeen;

    public SessionTracker(DiscoveredSession discovered, SessionSourceOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        Discovered = discovered;
        _firstSeen = time.GetUtcNow();
        _parser = discovered.Agent switch
        {
            AgentKind.Copilot => new CopilotParser(_state, time, options.CopilotRemoteApproval),
            AgentKind.Claude => new ClaudeParser(_state, time),
            _ => new CodexParser(_state, time),
        };
    }

    public DiscoveredSession Discovered { get; private set; }

    public Session Update(DiscoveredSession discovered)
    {
        Discovered = discovered;
        if (discovered.TranscriptPath is { } path)
        {
            if (_tail is null || !string.Equals(_tail.Path, path, StringComparison.Ordinal))
            {
                _tail = new TranscriptTail(path, _options);
                _parser.Reset();
            }
            _tail.Read(_parser);
        }
        if (discovered.StateDirectory is { } dir) { RefreshWorkspace(Path.Combine(dir, "workspace.yaml")); }
        return Settle(Build());
    }

    // updatedAt moves only when something a client shows changed. An event can be of a
    // kind that usually matters (a turn starting) and still change nothing (the session
    // was already working); then the session, and its updatedAt, stay exactly as they
    // were, so no upsert goes out for it. A sub-agent's line changing alone is not such a
    // change either (subagents.md §5): busy delegates would flood upserts. The line shown
    // stays as it was until something else changes.
    Session Settle(Session built)
    {
        var shown = JsonSerializer.Serialize(built with { UpdatedAt = default, Subagents = WithoutLines(built.Subagents) }, ProtocolJson.Options);
        if (_lastShown == shown && _lastUpdatedAt is { } kept) { return built with { UpdatedAt = kept, Subagents = _lastSubagents }; }
        // A change the transcript did not date (a registration's status, say) is dated now.
        if (_lastUpdatedAt is { } previous && built.UpdatedAt <= previous) { built = built with { UpdatedAt = _time.GetUtcNow() }; }
        _lastShown = shown;
        _lastUpdatedAt = built.UpdatedAt;
        _lastSubagents = built.Subagents;
        return built;
    }

    static IReadOnlyList<Subagent>? WithoutLines(IReadOnlyList<Subagent>? list) =>
        list is null ? null : [.. list.Select(a => a with { Line = null })];

    void RefreshWorkspace(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { _workspace = null; return; }
            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (stamp == _workspaceStamp && _workspace is not null) { return; }
            _workspace = Workspace.Read(path);
            _workspaceStamp = stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    Session Build()
    {
        var d = Discovered;
        var reg = d.Registration;
        var now = _time.GetUtcNow();
        var status = _state.Status;
        // Status precedence (docs/design/subagents.md §4): the session's own question wins over
        // a sub-agent's (rule 1, "question wins"), and a sub-agent's makes it waiting too (rule 2).
        var question = _state.Pending.LastOrDefault(q => q.Agent is null) ?? (_state.Pending.Count > 0 ? _state.Pending[^1] : null);
        // Delivery types only into the main agent's ask_user (AnswerService refuses a stamped
        // call), so nothing proves where keys for a sub-agent's would land: it is answered at
        // the machine. Nor for a Copilot form while a permission prompt or a sub-agent's
        // question is also pending: the CLI shows one of them, and a sub-agent's permission
        // dialog can be the one on screen while the session's own question is the one listed.
        // A permission prompt carries its own proof (CopilotPermission).
        if (question is { Kind: QuestionKind.Form } && (question.Agent is not null
            || (d.Agent == AgentKind.Copilot && _state.Pending.Any(q => q != question && (q.Kind == QuestionKind.Approval || q.Agent is not null)))))
        {
            question = question with { AnswerAtMachine = true };
        }
        DateTimeOffset? hintAt = null;

        if (d.Agent == AgentKind.Claude && reg?.HookStatusAt is { } hookAt && (_state.LastActivityAt is not { } last || hookAt > last))
        {
            // agent-ha-bridge's hooks report turn ends and permission prompts the
            // transcript cannot; trusted only when newer than anything the transcript said.
            switch (reg.HookStatus)
            {
                case "idle": status = SessionStatus.Idle; hintAt = hookAt; break;
                case "working": status = SessionStatus.Working; hintAt = hookAt; break;
                case "waiting" when question is null && _state.Tools.Current() is { } tool:
                    // A permission prompt, which agent-ha-bridge's Notification hook reports
                    // and only mirrors: it offers the reply box at best, never a proven
                    // answer, so it is answered at the machine.
                    question = QuestionMapper.Approval(tool.Id, tool.Summary is null ? $"Allow **{tool.Name}**?" : $"Allow **{tool.Name}**?\n\n`{tool.Summary}`", hookAt, atMachine: true);
                    hintAt = hookAt;
                    break;
            }
        }
        if (d.Agent == AgentKind.Codex && reg is not null)
        {
            // The approval agent-ha-bridge's hook recorded is the one that can be answered:
            // its generation is what is checked before typing. It stands for whatever the
            // rollout shows pending, which is the same prompt. One that agent-ha-bridge has
            // already typed into is never typed into again, so it is answered at the machine.
            if (reg.Approval is { } marker && (question is null || question.Kind == QuestionKind.Approval))
            {
                question = QuestionMapper.Approval(marker.DecisionId, marker.Question, marker.Created ?? reg.Updated ?? now, atMachine: marker.Attempted);
                hintAt = question.AskedAt;
            }
            // Codex's hooks report status directly, at UserPromptSubmit and Stop.
            if (reg.Updated is { } updated && (_state.LastActivityAt is not { } lastActivity || updated > lastActivity))
            {
                switch (reg.Status)
                {
                    case "idle": status = SessionStatus.Idle; hintAt ??= updated; break;
                    case "working" or "waiting": status = SessionStatus.Working; hintAt ??= updated; break;
                }
            }
        }

        var effective = question is not null ? SessionStatus.Waiting : status;
        DateTimeOffset since;
        if (_lastStatus == effective)
        {
            // Same status as last read, but it may have ended and begun again in between
            // (a whole turn appended between two reads): the transcript's own time for the
            // current status, when later, is when it began.
            since = _lastSince;
            DateTimeOffset? began = effective == _state.Effective ? _state.EffectiveSince
                : effective == SessionStatus.Waiting ? question?.AskedAt : null;
            if (began is { } b && b > since) { since = b; }
        }
        else
        {
            since = effective == _state.Effective && _state.EffectiveSince is { } s ? s
                : effective == SessionStatus.Waiting && question is not null ? question.AskedAt
                : hintAt ?? (_lastStatus is null ? _state.UpdatedAt ?? _firstSeen : now);
            // Never earlier than the status it replaces.
            if (_lastStatus is not null && since < _lastSince) { since = _lastSince; }
        }
        _lastStatus = effective;
        _lastSince = since;

        var ws = _workspace;
        var title = d.Agent switch
        {
            AgentKind.Copilot => ws?.Name ?? _state.FirstUserMessage,
            AgentKind.Claude => _state.AgentTitle ?? _state.FirstUserMessage,
            _ => _state.FirstUserMessage,
        };
        var (response, responseCut) = Limits.KeepEnd(_state.Response, Limits.Response);
        var history = _state.Tools.Count > 0 ? _state.Tools.Latest(Limits.History) : null;
        var (agents, shells) = _state.Background.Running(d.ProcessStartedAt);
        var (subagents, more) = _state.Background.Entries(d.ProcessStartedAt, Waiting());
        var source = _state.ResponseBy is null && _state.ReasoningBy is null ? null
            : new ActivitySource { Response = _state.ResponseBy, Reasoning = _state.ReasoningBy };
        OwnActivity? own = null;
        if (source is not null)
        {
            var (ownResponse, ownCut) = source.Response is null ? (null, false) : Limits.KeepEnd(_state.OwnResponse, Limits.Response);
            var ownReasoning = source.Reasoning is null ? null : Limits.KeepEnd(_state.OwnReasoning, Limits.Reasoning).Text;
            if (ownResponse is not null || ownReasoning is not null)
            {
                own = new OwnActivity { Response = ownResponse, ResponseTruncated = ownCut ? true : null, Reasoning = ownReasoning, At = _state.OwnAt };
            }
        }

        return new Session
        {
            Id = d.Id,
            Agent = d.Agent,
            Title = Text.Title(title),
            Cwd = ws?.Cwd ?? _state.Cwd ?? reg?.WorkingDirectory,
            Repository = ws?.Repository ?? _state.Repository,
            Branch = ws?.Branch ?? _state.Branch,
            Model = _state.Model ?? Text.NonBlank(reg?.Model),
            Status = effective,
            StatusSince = since,
            StartedAt = ws?.CreatedAt ?? _state.StartedAt,
            UpdatedAt = _state.UpdatedAt ?? reg?.Updated ?? ws?.UpdatedAt ?? _firstSeen,
            ProcessId = d.ProcessId,
            Question = question,
            WaitingOn = question?.Agent,
            Subagents = subagents,
            SubagentsMore = more,
            Activity = new Activity
            {
                Response = response,
                ResponseTruncated = responseCut ? true : null,
                Reasoning = Limits.KeepEnd(_state.Reasoning, Limits.Reasoning).Text,
                CurrentTool = effective == SessionStatus.Working ? _state.Tools.Current() : null,
                History = history,
                UserMessage = Limits.KeepStart(_state.UserMessage, Limits.UserMessage),
                TurnCount = _state.Seen ? Math.Max(0, _state.Prompts - (effective == SessionStatus.Idle ? 0 : 1)) : null,
                Compactions = _state.Seen ? _state.Compactions : null,
                Background = Protocol.Background.Of(agents, shells),
                Source = source,
                Own = own,
            },
        };
    }

    // The sub-agents with a question pending.
    HashSet<string> Waiting() => [.. _state.Pending.Where(q => q.Agent is not null).Select(q => q.Agent!)];

    // session.get { id, agent }: one sub-agent with its own words and tools, or null when the
    // session has none by that id.
    public Subagent? Agent(string agentId) =>
        _state.Background.Detail(agentId, Discovered.ProcessStartedAt, Waiting()) is { } detail ? SessionBudget.Fit(detail) : null;
}
