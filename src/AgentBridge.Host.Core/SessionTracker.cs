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
    ClaudeSubagents? _subagents;
    Workspace? _workspace;
    (DateTime, long) _workspaceStamp;
    SessionStatus? _lastStatus;
    DateTimeOffset _lastSince;
    string? _lastShown;
    DateTimeOffset? _lastUpdatedAt;
    IReadOnlyList<Subagent>? _lastSubagents;
    DateTimeOffset _firstSeen;
    // The last session built, and the nested agents' records it was built with.
    Session? _built;
    // The restore boundary the last build was made with (QuestionsFrom).
    DateTimeOffset? _builtFrom;
    // The boundary the hook status was last looked at with, and the interrupted run's hook status
    // time it found, if any.
    DateTimeOffset? _hookFrom;
    (DateTimeOffset At, string? Tool)? _staleHook;
    // The restore boundary the session last built set the interrupted run's questions aside by.
    public DateTimeOffset? BuiltFrom => _builtFrom;
    IReadOnlyList<ClaudeSubagents.Record> _nested = [];
    // What the last build asked Stalled, while the session could be called Not responding: null
    // when it could not (not working, or already showing another attention).
    (SessionStatus Effective, DateTimeOffset? LastEvent, bool Background, IReadOnlyList<Subagent>? Subagents, int? More)? _stall;

    public SessionTracker(DiscoveredSession discovered, SessionSourceOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        Discovered = discovered;
        _firstSeen = time.GetUtcNow();
        _parser = discovered.Agent switch
        {
            AgentKind.Copilot => new CopilotParser(_state, time, options.CopilotRemoteApproval, options.EscapedApproval),
            AgentKind.Claude => new ClaudeParser(_state, time),
            _ => new CodexParser(_state, time),
        };
    }

    public DiscoveredSession Discovered { get; private set; }

    // Tests: the transcript being followed.
    internal TranscriptTail? Tail => _tail;

    public Session Update(DiscoveredSession discovered)
    {
        // What Build reads: the parsed transcript, its nested agents, discovery's view and the
        // workspace, and for a working session the clock and its delegates' transcripts. Every pass
        // rebuilt and re-serialised every session, changed or not: about 35 MB/min of the host's
        // allocation (#608).
        var changed = _built is null || discovered != Discovered;
        Discovered = discovered;
        if (discovered.TranscriptPath is { } path)
        {
            if (_tail is null || !string.Equals(_tail.Path, path, StringComparison.Ordinal))
            {
                _tail = new TranscriptTail(path, _options);
                _subagents = null;
                _nested = [];
                _parser.Reset();
                changed = true;
            }
            // Unreadable too: a read can parse new lines and then fail (the anchor after them), and
            // the parser has moved though the read says it failed. A transcript that stays
            // unreadable is rebuilt each pass, which is rare and costs no revision (the JSON is
            // the same).
            changed |= _tail.Read(_parser) is TailOutcome.Appended or TailOutcome.Restarted or TailOutcome.Unreadable;
            // A nested Claude delegate's start and parent are written only beside the session's
            // transcript; one can exist only once a delegate of the session's does.
            if (_parser is ClaudeParser claude && _state.Background.HasAgents)
            {
                _subagents ??= new ClaudeSubagents();
                var nested = _subagents.Read(path);
                // What Nested took counts as well as which records there are: a child sorted before
                // its parent (spawnDepth left out reads as 2) is taken a pass later, from the same
                // records (#634).
                foreach (var record in nested) { changed |= claude.Nested(record); }
                if (!nested.SequenceEqual(_nested)) { (_nested, changed) = (nested, true); }
            }
        }
        if (discovered.StateDirectory is { } dir)
        {
            var workspace = _workspace;
            RefreshWorkspace(Path.Combine(dir, "workspace.yaml"));
            changed |= !ReferenceEquals(workspace, _workspace);
        }
        // Not responding (#800) comes from the clock and from the delegates' own transcripts, which
        // nothing above looks at: a hung session is exactly one whose transcript stops changing, so
        // skipping its build left it working with no attention for ever (#634 review, H1). Asked
        // again with what the last build asked it; only a different answer builds the session.
        if (!changed && _stall is { } stall
            && Stalled(stall.Effective, stall.LastEvent, stall.Background, stall.Subagents, stall.More, _time.GetUtcNow()) != _built!.Attention)
        {
            changed = true;
        }
        // A restore's boundary (QuestionsFrom) set or moved since the last build changes what it
        // shows as asked (#780), though nothing it reads did.
        // The hook status a restored Claude session's registration carries when the boundary is
        // first read with, from before it, is the interrupted run's: SessionStart keeps the last
        // status recorded (as agent-ha-bridge's hook does), so its old "waiting" read as the new
        // run asking (Copilot on #837). Told by being that same record, not by its time; a hook
        // event since is the new run's.
        if (_hookFrom != _state.AskedFrom)
        {
            _hookFrom = _state.AskedFrom;
            // After a host restart, by identity: the stale one is the one an earlier run set aside, by
            // its tool and its time, so a different approval a clock set back gave the same time is
            // not taken for it (Engineering, cr2-837h). A record from before kept the time alone.
            var tool = _state.Tools.Current()?.Id;
            _staleHook = null;
            if (_state.AskedFrom is { } from && discovered.Registration?.HookStatusAt is { } hook)
            {
                if (_state.Known is { } known) { _staleHook = KnownHook(known, hook, tool); }
                else if (hook < from) { _staleHook = (hook, tool); }
            }
            changed = true;
        }
        changed |= _builtFrom != _state.AskedFrom;
        _state.EndBacklog();
        if (!changed) { return _built!; }
        Builds++;
        _builtFrom = _state.AskedFrom;
        return _built = Settle(Build());
    }

    // Tests: how many times the session was built.
    internal int Builds { get; private set; }

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
        // Without what an interrupted run left asking (a restored session's boundary, #780): it went
        // with that run, and must neither hide what is asked now nor hold the status at waiting.
        var pending = _state.Asking;
        // Status precedence (docs/design/subagents.md §4): the session's own question wins over
        // a sub-agent's (rule 1, "question wins"), and a sub-agent's makes it waiting too (rule 2).
        var question = pending.LastOrDefault(q => q.Agent is null) ?? (pending.Count > 0 ? pending[^1] : null);
        // Copilot asks a permission for each of a batch of parallel tool calls at once, and its
        // one dialog shows them one at a time; seen answered at the machine in the order asked
        // (#690). Listing the newest offered Approve once for a request the screen was not
        // showing, so the earliest is listed, saying how many wait; each is its own question,
        // so an answer meant for one never reaches the next. Delivery still types only when
        // the screen shows that request and none of the others.
        if (d.Agent == AgentKind.Copilot && question is { Kind: QuestionKind.Approval })
        {
            var approvals = pending.Where(q => q.Kind == QuestionKind.Approval).ToList();
            if (approvals.Count > 1)
            {
                // One whose dialog the host cannot tell from the others' is never typed for while
                // they wait (CopilotPermissionScreen), so it is shown as answered at the machine.
                question = approvals[0] with { Waiting = approvals.Count };
                if (_state.Unidentified.Contains(question.Id)) { question = question with { AnswerAtMachine = true }; }
            }
        }
        // Delivery types only into the main agent's ask_user (AnswerService refuses a stamped
        // call), so nothing proves where keys for a sub-agent's would land: it is answered at
        // the machine. Nor for a Copilot form while a permission prompt or a sub-agent's
        // question is also pending: the CLI shows one of them, and a sub-agent's permission
        // dialog can be the one on screen while the session's own question is the one listed.
        // A permission prompt carries its own proof (CopilotPermission).
        if (question is { Kind: QuestionKind.Form } && (question.Agent is not null
            || (d.Agent == AgentKind.Copilot && pending.Any(q => q != question && (q.Kind == QuestionKind.Approval || q.Agent is not null)))))
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
                case "waiting" when question is null && _state.Tools.Current() is { } tool && !(_staleHook is { } stale && stale.At == hookAt && (stale.Tool is null || stale.Tool == tool.Id)):
                    // A permission prompt, which agent-ha-bridge's Notification hook reports
                    // and only mirrors: it offers the reply box at best, never a proven
                    // answer, so it is answered at the machine.
                    question = QuestionMapper.Approval(tool.Id, tool.Summary is null ? $"Allow **{CopilotPermission.Plain(tool.Name)}**?" : $"Allow **{CopilotPermission.Plain(tool.Name)}**?\n\n{CopilotPermission.Fence(tool.Summary)}", hookAt, atMachine: true);
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
                question = QuestionMapper.CodexApproval(marker.DecisionId, marker.Tool, marker.Command, marker.CommandCut, marker.Question, marker.Created ?? reg.Updated ?? now, atMachine: marker.Attempted, approve: _options.EscapedApproval);
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
        TurnStatus = status;
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
        // The main agent's own text for each field whose top-level text is not it: a
        // sub-agent's (source names it), or none at all where the host has the main agent's
        // only from the part of the transcript it skimmed.
        var (ownResponse, ownCut) = source?.Response is not null || _state.Response is null ? Limits.KeepEnd(_state.OwnResponse, Limits.Response) : (null, false);
        ownCut |= ownResponse is not null && _state.OwnResponseCut;
        var ownReasoning = source?.Reasoning is not null || _state.Reasoning is null ? Limits.KeepEnd(_state.OwnReasoning, Limits.Reasoning).Text : null;
        var own = ownResponse is null && ownReasoning is null ? null
            : new OwnActivity { Response = ownResponse, ResponseTruncated = ownCut ? true : null, Reasoning = ownReasoning, At = _state.OwnAt };
        var lastEvent = Later(_state.LastEventAt, hintAt);
        var background = agents + shells > 0;
        _stall = _state.Attention is null && effective == SessionStatus.Working ? (effective, lastEvent, background, subagents, more) : null;

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
            Attention = _state.Attention ?? Stalled(effective, lastEvent, background, subagents, more, now),
            Activity = new Activity
            {
                Response = response,
                ResponseTruncated = responseCut ? true : null,
                Reasoning = Limits.KeepEnd(_state.Reasoning, Limits.Reasoning).Text,
                CurrentTool = effective == SessionStatus.Working ? _state.Tools.Current() : null,
                History = history,
                UserMessage = Limits.KeepStart(_state.UserMessage, Limits.UserMessage),
                UserMessageFrom = _state.UserMessage is null ? null : _state.UserMessageFrom,
                UserMessageLabel = _state.UserMessage is null ? null : Limits.KeepStart(_state.UserMessageLabel, Limits.UserMessageLabel),
                TurnCount = _state.Seen ? Math.Max(0, _state.Prompts - (effective == SessionStatus.Idle || _state.UncountedTurn ? 0 : 1)) : null,
                Compactions = _state.Seen ? _state.Compactions : null,
                Background = Protocol.Background.Of(agents, shells),
                Source = source,
                Own = own,
            },
        };
    }

    // Not responding (#800): still working, and nothing at all written for longer than a turn ever
    // goes quiet. A 401 that the CLI never writes, or a CLI hung on its own network call, left a
    // session reading working for an hour with nothing to say it had stopped. Never over a
    // question (it is waiting on the person) or another attention; any event clears it.
    Attention? Stalled(SessionStatus effective, DateTimeOffset? lastEvent, bool background, IReadOnlyList<Subagent>? subagents, int? more, DateTimeOffset now)
    {
        if (effective != SessionStatus.Working || lastEvent is not { } last) { return null; }
        var busy = background || _state.Tools.Current() is not null;
        var after = busy ? _options.NotRespondingBusyAfter : _options.NotRespondingAfter;
        if (now - last < after) { return null; }
        // A delegate that writes only its own transcript (Claude's beside the session's, Codex's a
        // rollout of its own) leaves the session's quiet while it works. Its last event, read from
        // that transcript, counts as the session's; one that cannot be checked never lets it read
        // stalled: a false Not responding is worse than a late one.
        if (DelegatesAlive(subagents, more, now, after)) { return null; }
        var agent = Discovered.Agent;
        return new Attention
        {
            Kind = AttentionKind.NotResponding,
            Since = last,
            Agent = agent,
            Message = AgentErrors.Template(AttentionKind.NotResponding, null, null, agent),
            LastEventAt = last,
        };
    }

    static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    // Whether the session's working delegates keep it alive (Claude's and Codex's: Copilot's write
    // into the session's own events, already counted): one wrote an event within `after`, or not
    // every one could be checked (more working than listed, a transcript not found or unread).
    // Read only once the session already looks stalled.
    bool DelegatesAlive(IReadOnlyList<Subagent>? subagents, int? more, DateTimeOffset now, TimeSpan after)
    {
        if (Discovered.Agent is not (AgentKind.Claude or AgentKind.Codex) || subagents is null) { return false; }
        var working = subagents.Where(a => a.Status == SubagentStatus.Working).ToList();
        if (working.Count == 0) { return false; }
        // Working delegates are listed first: with every listed one working and more left out, the
        // one still writing may be one that was not listed.
        if (more > 0 && working.Count == subagents.Count) { return true; }
        foreach (var a in working)
        {
            if (DelegateLastEvent(a) is not { } at || now - at < after) { return true; }
        }
        return false;
    }

    // What each delegate transcript's last event was, by the length it was read at: a stalled
    // session is checked every pass, and a long transcript is not read again unchanged.
    readonly Dictionary<string, (long Length, DateTimeOffset? At)> _delegateEvents = new(StringComparer.Ordinal);

    // The last event a delegate wrote: the timestamp of the last complete record of its own
    // transcript, never the file's write time, which Windows leaves stale while the writer holds
    // the file open (CodexRollout.IsOwnThread). Null when it cannot be found or read.
    DateTimeOffset? DelegateLastEvent(Subagent a)
    {
        if (Discovered.TranscriptPath is not { } transcript || Path.GetDirectoryName(transcript) is not { } here) { return null; }
        if (a.Id.Length == 0 || !a.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { return null; }
        try
        {
            string? path;
            if (Discovered.Agent == AgentKind.Claude)
            {
                path = Path.Combine(here, Path.GetFileNameWithoutExtension(transcript), "subagents", $"agent-{a.Id}.jsonl");
                if (!File.Exists(path)) { return null; }
            }
            else
            {
                // A Codex sub-agent's rollout-<time>-<thread id>.jsonl, in the day folder it started
                // in: the session's, or its own.
                var days = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)));
                var day = a.StartedAt is { } started && days is not null
                    ? Path.Combine(days, started.UtcDateTime.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture)) : null;
                path = new[] { here, day }.OfType<string>().Distinct().Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, $"rollout-*-{a.Id}.jsonl")).FirstOrDefault();
                if (path is null) { return null; }
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_delegateEvents.TryGetValue(path, out var known) && known.Length == stream.Length) { return known.At; }
            var at = CodexRollout.LastTimestamp(stream);
            if (_delegateEvents.Count > ClaudeSubagents.MaxRecords) { _delegateEvents.Clear(); }
            _delegateEvents[path] = (stream.Length, at);
            return at;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Which prompt the session's userMessage is, for the driver (#489): its record where the parser
    // knows it, beside its words, so the same words sent again are another prompt.
    public string? PromptKey => _state.UserMessage is { } text ? (_state.UserMessageKey ?? "") + "\n" + text : null;

    // How many prompts the transcript has had, so the driver can tell when more than one came in one
    // read, and so cannot know which of them a reply's arm was for.
    public int Prompts => _state.Prompts;
    // The turn's own status, before a pending question makes it Waiting: what a restored session
    // shows once a question the interrupted run left is set aside (SessionSource.Commit, #780).
    public SessionStatus TurnStatus { get; private set; }
    public DateTimeOffset? LastPromptAt => _state.LastPromptAt;

    // The sub-agents with a question pending.
    HashSet<string> Waiting() => [.. _state.Asking.Where(q => q.Agent is not null).Select(q => q.Agent!)];

    // The restored session's boundary (SessionState.AskedFrom), set before each read.
    public DateTimeOffset? QuestionsFrom
    {
        get => _state.AskedFrom;
        set => _state.AskedFrom = value;
    }

    // The boundary with the interrupted run's questions as an earlier host run found them
    // (SessionState.Known), the ids set first so the boundary is set with them.
    public void SetQuestionsFrom(DateTimeOffset? from, IReadOnlySet<string>? known)
    {
        if (from != _state.AskedFrom) { _state.Known = known; }
        _state.AskedFrom = from;
    }

    // The interrupted run's questions set aside and still unanswered, by id, oldest first, and a stale
    // hook approval by its tool and time: the newest RestoreFiles.MaxInterrupted of them. Answered ones
    // are not kept: with them, a long session went past the bound, and the record then fell back to
    // telling them by time, which hid a new question a clock set back (Engineering, cr2-837h). Past the
    // bound, the oldest are let go and so read as new after a restart: shown at the machine, never a
    // new question hidden.
    public IReadOnlyList<string> InterruptedQuestions
    {
        get
        {
            var ids = _state.Pending.Select(q => q.Id).Where(_state.Interrupted.Contains).Distinct(StringComparer.Ordinal).ToList();
            if (_staleHook is { } h) { ids.Add(HookKey(h.At, h.Tool)); }
            var max = AgentBridge.Host.Core.Restore.RestoreFiles.MaxInterrupted;
            return ids.Count > max ? [.. ids.Skip(ids.Count - max)] : ids;
        }
    }

    // The stale hook approval an earlier run set aside, if the registration's is it. With the tool not
    // known on this pass (the transcript unreadable, or not there yet), the one set aside at that time
    // is taken with its tool from the key; nothing set it aside again later, so missing it here showed
    // the old approval once the transcript was read (Engineering, cr-837j).
    static (DateTimeOffset At, string? Tool)? KnownHook(IReadOnlySet<string> known, DateTimeOffset at, string? tool)
    {
        if (tool is not null && known.Contains(HookKey(at, tool))) { return (at, tool); }
        if (known.Contains(HookKey(at, null))) { return (at, tool); }
        if (tool is not null) { return null; }
        var suffix = "@" + HookKey(at, null)["hook:".Length..];
        var key = known.Where(k => k.StartsWith("hook:", StringComparison.Ordinal) && k.EndsWith(suffix, StringComparison.Ordinal) && k.Length > "hook:".Length + suffix.Length)
            .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        return key is null ? null : (at, key["hook:".Length..^suffix.Length]);
    }

    static string HookKey(DateTimeOffset at, string? tool) =>
        "hook:" + (tool is null ? "" : tool + "@") + at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    // session.get { id, agent }: one sub-agent with its own words and tools, or null when the
    // session has none by that id.
    public Subagent? Agent(string agentId) =>
        _state.Background.Detail(agentId, Discovered.ProcessStartedAt, Waiting()) is { } detail ? SessionBudget.Fit(detail) : null;
}
