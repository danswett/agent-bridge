using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentConversationsTests
{
    DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    AgentConversations Make(bool allowLaunchChain = false) => new(() => now, allowAgentLaunchedToLaunch: allowLaunchChain);

    [Fact]
    public void The_depth_table_holds_and_a_chain_may_return_to_a_session_already_in_it()
    {
        var c = Make();
        Assert.True(c.TrySend("A", "B").Allowed);
        Assert.True(c.TrySend("B", "C").Allowed);
        var refused = c.TrySend("C", "D");
        Assert.False(refused.Allowed);
        Assert.Equal("chain-too-deep", refused.Reason);
        Assert.True(c.TrySend("C", "A").Allowed);
    }

    [Fact]
    public void A_launch_is_a_hop_so_a_session_reached_through_two_others_cannot_launch()
    {
        var c = Make();
        Assert.True(c.TrySend("A", "B").Allowed);
        Assert.True(c.TryLaunch("B", "L").Allowed);

        var deep = Make();
        deep.TrySend("A", "B");
        deep.TrySend("B", "C");
        Assert.Equal("chain-too-deep", deep.TryLaunch("C", "L").Reason);
    }

    [Fact]
    public void A_and_B_keep_two_via_entries_and_the_twenty_first_message_is_refused()
    {
        var c = Make();
        AgentConversationVerdict last = default;
        for (var i = 0; i < 20; i++)
        {
            last = i % 2 == 0 ? c.TrySend("A", "B") : c.TrySend("B", "A");
            Assert.True(last.Allowed);
        }
        Assert.Equal(["A", "B"], last.Conversation!.Via);
        Assert.Equal(20, last.Conversation.Messages);

        var refused = c.TrySend("A", "B");
        Assert.Equal("conversation-budget", refused.Reason);
    }

    [Fact]
    public void A_conversation_older_than_the_limit_is_refused_but_a_person_reply_starts_a_fresh_one()
    {
        var c = Make();
        Assert.True(c.TrySend("A", "B").Allowed);
        now = now.AddMinutes(60);
        Assert.True(c.TrySend("B", "A").Allowed);
        now = now.AddSeconds(1);
        Assert.Equal("conversation-budget", c.TrySend("A", "B").Reason);

        c.RecordTurn("A", AgentTurnDriver.Person);
        var fresh = c.TrySend("A", "B");
        Assert.True(fresh.Allowed);
        Assert.Equal(1, fresh.Conversation!.Messages);
    }

    [Fact]
    public void A_person_reply_resets_a_spent_budget_but_a_tool_approval_does_not()
    {
        var c = Make();
        for (var i = 0; i < 20; i++) { c.TrySend(i % 2 == 0 ? "A" : "B", i % 2 == 0 ? "B" : "A"); }

        c.RecordTurn("A", AgentTurnDriver.Approval);
        Assert.Equal("conversation-budget", c.TrySend("A", "B").Reason);
        c.RecordTurn("A", AgentTurnDriver.Person);
        Assert.True(c.TrySend("A", "B").Allowed);
    }

    [Fact]
    public void A_session_an_agent_launched_cannot_launch_unless_allowed_and_the_refusal_costs_no_budget()
    {
        var c = Make();
        Assert.True(c.TryLaunch("A", "L").Allowed);
        Assert.Equal("launch-not-allowed", c.TryLaunch("L", "M").Reason);
        // L's refused launch used no message: its conversation is still the one A started.
        Assert.Equal(2, c.TrySend("L", "A").Conversation!.Messages);

        var open = Make(allowLaunchChain: true);
        open.TryLaunch("A", "L");
        Assert.True(open.TryLaunch("L", "M").Allowed);
    }

    [Fact]
    public void A_full_table_refuses_a_new_session_rather_than_forget_a_live_one_and_makes_room_by_age_or_when_a_session_ends()
    {
        var c = Make();
        for (var i = 0; i < 1024; i++) { Assert.True(c.TrySend("A", $"t{i}").Allowed); }
        Assert.Equal("conversation-budget", c.TrySend("A", "one-too-many").Reason);

        c.SessionEnded("t0");
        Assert.True(c.TrySend("B", "one-too-many").Allowed);
        Assert.Equal("conversation-budget", c.TrySend("B", "another").Reason);

        now = now.AddMinutes(61);
        Assert.True(c.TrySend("B", "another").Allowed);
    }
}
