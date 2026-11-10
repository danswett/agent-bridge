using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

public sealed class ClosedQuestionTests
{
    [Fact]
    public void Only_a_confirmed_local_delivery_says_you_answered_and_keeps_the_chosen_label()
    {
        var sent = new Delivery { Key = "answer|s1|q1", Request = "r1", State = DeliveryState.Done, Outcome = "delivered", Chosen = "PostgreSQL" };
        var closed = new ClosedQuestion(Make.Choice(), sent);

        Assert.Equal("You answered · \u2068PostgreSQL\u2069", closed.Summary);
        Assert.Equal("Question answered", closed.Announcement);
        Assert.Equal("Question closed", new ClosedQuestion(Make.Choice()).Summary);
        Assert.Equal(ReceiptWords.AnswerUnconfirmed, new ClosedQuestion(Make.Choice(), sent with { Outcome = "unconfirmed" }).Summary);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("stale")]
    [InlineData("busy")]
    [InlineData("offline")]
    public void A_closed_question_discards_a_refusal_or_other_obsolete_failure(string outcome)
    {
        var delivery = new Delivery { Key = "answer|s1|q1", Request = "r1", State = DeliveryState.Done, Outcome = outcome, Reason = "old refusal" };

        var closed = new ClosedQuestion(Make.Choice(), delivery);

        Assert.Equal("Question closed", closed.Summary);
        Assert.Equal("Question closed", closed.Announcement);
    }

    [Fact]
    public void An_approvals_collapsed_line_contains_none_of_its_message_and_its_record_uses_the_existing_literal_renderer()
    {
        var asked = Make.Choice() with { Kind = QuestionKind.Approval, Message = "**shell**\n```\nrun\u202Ethis\n```" };
        var closed = new ClosedQuestion(asked);

        Assert.Equal("Question closed", closed.Summary);
        Assert.Equal("Permission request", closed.Heading);
        Assert.True(closed.Approval);
        Assert.DoesNotContain("\u202E", closed.Message);
        Assert.Contains("\\u202E", closed.Message);
        var block = Assert.IsType<MdCode>(Assert.Single(MarkdownModel.Approval(closed.Message)));
        Assert.Contains("**shell**", block.Text);
        Assert.Contains("\\u202E", block.Text);
    }
}
