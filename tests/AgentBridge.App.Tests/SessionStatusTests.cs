using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// What a session row says: docs/design/ui.md §2.2, one test (or more) per rule, named after it,
// as pwa/test/unit/status.test.mjs has them.
public sealed class SessionStatusTests
{
    static StatusLine Said(Session s) => StatusRules.For(s);

    static Session Session(SessionStatus status = SessionStatus.Idle, Background? background = null, Question? question = null,
        IReadOnlyList<Subagent>? subagents = null, int? more = null, string? waitingOn = null, AgentKind agent = AgentKind.Copilot) => Make.Session(status: status, background: background, question: question, title: "Plan Migration to Windows App") with
        {
            Agent = agent,
            Status = question is not null ? SessionStatus.Waiting : status,
            Subagents = subagents,
            SubagentsMore = more,
            WaitingOn = waitingOn,
        };

    static Subagent Delegate(string id, SubagentKind kind = SubagentKind.Background, SubagentStatus status = SubagentStatus.Working, string? parent = null, string? name = null) =>
        new() { Id = id, Kind = kind, Status = status, Parent = parent, Name = name ?? $"Delegate {id}" };

    // The protocol's worked example (docs/protocol/examples/session-subagents.json): a delegate's
    // permission prompt while another delegate works and a nested task has finished.
    static Session Example(bool atMachine = false) => Session(
        background: Background.Of(2, 0),
        question: Make.Approval() with { Agent = "agent-c204", AnswerAtMachine = atMachine ? true : null },
        subagents:
        [
            Delegate("agent-c204", status: SubagentStatus.Waiting, name: "Read witness shell logic"),
            Delegate("agent-3a91", name: "Run full test suite"),
            Delegate("agent-7e55", SubagentKind.Task, SubagentStatus.Ended, parent: "agent-c204", name: "Review the witness change") with { Outcome = SubagentOutcome.Done },
        ],
        more: 2,
        waitingOn: "agent-c204");

    [Fact]
    public void Rule_1_the_sessions_own_question_reads_Waiting_on_you_with_the_needs_glyph_in_needs_words()
    {
        var s = Said(Session(question: Make.Free()));
        Assert.Equal((1, StatusGlyph.Needs, "Waiting on you", StatusTone.Needs), (s.Rule, s.Glyph, s.Text, s.Tone));
    }

    [Fact]
    public void Rule_1_delegates_still_running_beside_the_question_are_counted_with_a_number_before_the_noun()
    {
        Assert.Equal("Waiting on you · 2 delegates still running", Said(Session(question: Make.Free(), background: Background.Of(2, 1))).Text);
        Assert.Equal("Waiting on you · 1 delegate still running", Said(Session(question: Make.Free(), subagents: [Delegate("a", SubagentKind.Task), Delegate("b", status: SubagentStatus.Ended)])).Text);
        // An idle background delegate waits to be woken: it is not running.
        Assert.Equal("Waiting on you", Said(Session(question: Make.Free(), subagents: [Delegate("a", status: SubagentStatus.Idle)])).Text);
    }

    [Fact]
    public void Rule_1_a_question_only_the_machine_can_answer_says_to_answer_it_there()
    {
        Assert.Equal("Waiting on you · answer at the machine", Said(Session(question: Make.Approval() with { AnswerAtMachine = true })).Text);
        Assert.Equal("Waiting on you · 1 delegate still running · answer at the machine", Said(Session(question: Make.Approval() with { AnswerAtMachine = true }, background: Background.Of(1, 0))).Text);
    }

    [Fact]
    public void Rule_1_wins_over_rule_2_the_sessions_own_question_is_the_sessions_even_while_a_delegate_waits_too()
    {
        var own = Example() with { WaitingOn = null, Question = Make.Free() };
        Assert.Equal((1, "Waiting on you · 2 delegates still running"), (Said(own).Rule, Said(own).Text));
    }

    [Fact]
    public void Rule_2_a_delegates_question_reads_name_asks_named_from_the_delegate_list()
    {
        var s = Said(Example());
        Assert.Equal((2, StatusGlyph.Needs, "Read witness shell logic asks", StatusTone.Needs), (s.Rule, s.Glyph, s.Text, s.Tone));
        // The question's agent names it as waitingOn does.
        Assert.Equal("Read witness shell logic asks", Said(Example() with { WaitingOn = null }).Text);
    }

    [Fact]
    public void Rule_2_a_delegates_question_only_the_machine_can_answer_reads_name_asks_answer_at_the_machine()
    {
        Assert.Equal("Read witness shell logic asks · answer at the machine", Said(Example(atMachine: true)).Text);
    }

    [Fact]
    public void Rule_2_a_delegate_the_list_left_out_still_asks_without_a_name_the_host_never_gave()
    {
        Assert.Equal("A delegate asks", Said(Example() with { Subagents = null, WaitingOn = "agent-gone" }).Text);
    }

    [Fact]
    public void Rule_3_the_main_agents_turn_reads_Working_with_the_working_glyph_in_plain_words()
    {
        var s = Said(Session(SessionStatus.Working, Background.Of(1, 2)));
        Assert.Equal((3, StatusGlyph.Working, "Working", StatusTone.Plain), (s.Rule, s.Glyph, s.Text, s.Tone));
    }

    [Fact]
    public void Rule_3_a_foreground_task_agent_running_reads_Working_task_name()
    {
        Assert.Equal("Working · task: Bisect RestartAfterResume", Said(Session(SessionStatus.Working, subagents:
        [
            Delegate("t2", SubagentKind.Task, parent: "t1", name: "Bisect RestartAfterResume"),
            Delegate("t1", SubagentKind.Task, name: "Find the flaky test"),
            Delegate("b1", name: "Run full test suite"),
        ])).Text);
    }

    [Fact]
    public void Rule_3_a_task_a_background_delegate_started_or_one_that_has_finished_is_not_the_main_agents()
    {
        Assert.Equal("Working", Said(Session(SessionStatus.Working, subagents:
        [
            Delegate("t9", SubagentKind.Task, parent: "b1", name: "Check the witness"),
            Delegate("b1", name: "Read witness shell logic"),
            Delegate("t1", SubagentKind.Task, SubagentStatus.Ended, name: "Done already"),
        ])).Text);
        // A parent the list left out can't be shown to be the main agent's.
        Assert.Equal("Working", Said(Session(SessionStatus.Working, subagents: [Delegate("t3", SubagentKind.Task, parent: "missing", name: "Orphan")])).Text);
    }

    [Fact]
    public void Rule_4_the_main_agent_idle_with_background_agents_reads_Delegating_to_N_agents_with_the_delegating_glyph()
    {
        var s = Said(Session(background: Background.Of(3, 1), subagents: [Delegate("a"), Delegate("b"), Delegate("c")]));
        Assert.Equal((4, StatusGlyph.Delegating, "Delegating to 3 agents", StatusTone.Plain), (s.Rule, s.Glyph, s.Text, s.Tone));
        Assert.Equal("Delegating to 1 agent", Said(Session(background: Background.Of(1, 0), subagents: [Delegate("a")])).Text);
    }

    [Fact]
    public void Rule_4_a_nested_delegate_counts_once_under_its_parent_and_a_list_cut_short_still_counts_them_all()
    {
        Assert.Equal("Delegating to 2 agents", Said(Session(background: Background.Of(2, 0), subagents: [Delegate("a"), Delegate("b", parent: "a")])).Text);
        Assert.Equal("Delegating to 5 agents", Said(Session(background: Background.Of(5, 0), more: 5)).Text);
    }

    [Fact]
    public void Rule_5_the_main_agent_idle_with_only_background_commands_reads_N_background_commands()
    {
        var s = Said(Session(background: Background.Of(0, 2)));
        Assert.Equal((5, StatusGlyph.Delegating, "2 background commands", StatusTone.Plain), (s.Rule, s.Glyph, s.Text, s.Tone));
        Assert.Equal("1 background command", Said(Session(background: Background.Of(0, 1))).Text);
    }

    [Fact]
    public void Rule_6_nothing_running_reads_Idle_with_the_idle_glyph()
    {
        var s = Said(Session());
        Assert.Equal((6, StatusGlyph.Idle, "Idle", StatusTone.Plain), (s.Rule, s.Glyph, s.Text, s.Tone));
    }

    [Fact]
    public void Rule_8_delegation_the_host_counts_but_cant_list_reads_Delegating_to_N_agents_details_not_reported()
    {
        var s = Said(Session(background: Background.Of(2, 0), agent: AgentKind.Claude));
        Assert.Equal((8, StatusGlyph.Delegating, "Delegating to 2 agents · details not reported", StatusTone.Plain), (s.Rule, s.Glyph, s.Text, s.Tone));
        // It refines rule 4 only: commands alone, or work alongside a turn, are their own rules.
        Assert.Equal(5, Said(Session(background: Background.Of(0, 1), agent: AgentKind.Claude)).Rule);
        Assert.Equal(3, Said(Session(SessionStatus.Working, Background.Of(1, 0), agent: AgentKind.Codex)).Rule);
    }

    // Rule 7 is an overlay: whatever rules 1-6 (and 8) chose stays, the badge is added, and the
    // words turn to the agent role.
    public static TheoryData<int> Rules => [1, 2, 3, 4, 5, 6, 8];

    static Session ForRule(int rule) => rule switch
    {
        1 => Session(question: Make.Free()),
        2 => Example(),
        3 => Session(SessionStatus.Working),
        4 => Session(background: Background.Of(2, 0), subagents: [Delegate("a"), Delegate("b")]),
        5 => Session(background: Background.Of(0, 1)),
        6 => Session(),
        _ => Session(background: Background.Of(1, 0), agent: AgentKind.Claude),
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Rule_7_over_each_rule_an_agent_driving_keeps_its_glyph_and_words_adds_the_driven_badge_and_puts_the_words_in_agent_colour(int rule)
    {
        var person = Said(ForRule(rule));
        var agent = Said(ForRule(rule) with { Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:0f1e2d3c" });
        Assert.Equal((rule, false), (person.Rule, person.Driven));
        Assert.Equal((person.Rule, person.Glyph, person.Text), (agent.Rule, agent.Glyph, agent.Text));
        Assert.Equal((true, StatusTone.Agent), (agent.Driven, agent.Tone));
    }

    [Fact]
    public void A_session_the_host_is_ending_reads_Ending_session_with_the_ended_glyph_in_muted_words_whatever_it_was_doing()
    {
        var s = Said(Session(SessionStatus.Ending, Background.Of(2, 0)));
        Assert.Equal((StatusRules.Ending, StatusGlyph.Ended, "Ending session…", StatusTone.Muted), (s.Rule, s.Glyph, s.Text, s.Tone));
        Assert.True(Said(Session(SessionStatus.Ending) with { Driver = DriverKind.Agent }).Driven);
    }

    [Fact]
    public void The_glyph_is_named_with_its_state_as_a_row_reads_title_status_machine()
    {
        Assert.Equal("Plan Migration to Windows App, delegating to 2 agents, DEV-VM1", Said(ForRule(4)).Label("Plan Migration to Windows App", "DEV-VM1"));
        Assert.Equal("Plan Migration to Windows App, Read witness shell logic asks, DEV-VM1", Said(ForRule(2)).Label("Plan Migration to Windows App", "DEV-VM1"));
        Assert.Equal("Plan Migration to Windows App, 1 background command, DEV-VM1", Said(ForRule(5)).Label("Plan Migration to Windows App", "DEV-VM1"));
        // The diamond is only a shape, so the name says it in words.
        Assert.Equal("Plan Migration to Windows App, working, driven by an agent, DEV-VM1", Said(ForRule(3) with { Driver = DriverKind.Agent }).Label("Plan Migration to Windows App", "DEV-VM1"));
        Assert.Equal("Untitled session, idle", Said(Session()).Label(null, null));
    }
}
