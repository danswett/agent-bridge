using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class ReviewerConstraintTests
{
    [Theory]
    [InlineData("5")]
    [InlineData("5.0")]
    [InlineData("5e0")]
    public async Task Equivalent_numeric_limits_keep_the_draft(string maximum)
    {
        var ui = new LockedDispatcher();
        await using var connection = Connection(ui);
        var deliveries = new Deliveries(connection, ui, new FakeTimeProvider(Make.T0));
        var first = Form("{\"maximum\":5}");
        var card = Card(first, deliveries);
        try
        {
            var original = card.Question!;
            original.Fields[0].Text = "3";
            var next = Form("{\"maximum\":" + maximum + "}");
            Assert.Null(first.Fields[0].Constraints!.NumberProblem("3"));
            Assert.Null(next.Fields[0].Constraints!.NumberProblem("3"));
            card.Update(Make.Session(question: next));
            Assert.Same(original, card.Question);
            Assert.Equal("3", card.Question!.Fields[0].Text);
        }
        finally { card.Detach(); }
    }

    [Fact]
    public void Reordered_constraint_keys_are_equal() =>
        Assert.True(QuestionViewModel.SaysTheSame(Form("{\"minimum\":1,\"maximum\":5}"), Form("{\"maximum\":5,\"minimum\":1}")));

    [Fact]
    public async Task Repeated_limit_changes_preserve_both_undismissed_drafts()
    {
        var ui = new LockedDispatcher();
        await using var connection = Connection(ui);
        var deliveries = new Deliveries(connection, ui, new FakeTimeProvider(Make.T0));
        var card = Card(Form("{\"maximum\":5}"), deliveries);
        try
        {
            card.Question!.Fields[0].Text = "3";
            card.Update(Make.Session(question: Form("{\"maximum\":4}")));
            Assert.Equal("3", card.OrphanText);
            card.Question!.Fields[0].Text = "2";
            card.Update(Make.Session(question: Form("{\"maximum\":3}")));
            Assert.Contains("3", card.OrphanText, StringComparison.Ordinal);
            Assert.Contains("2", card.OrphanText, StringComparison.Ordinal);
            card.Question!.Fields[0].Text = "1";
            card.Update(Make.Session());
            Assert.Contains("3", card.OrphanText, StringComparison.Ordinal);
            Assert.Contains("2", card.OrphanText, StringComparison.Ordinal);
            Assert.Contains("1", card.OrphanText, StringComparison.Ordinal);
        }
        finally { card.Detach(); }
    }

    static Question Form(string constraints) => Make.Form(new Field
    {
        Name = "count", Type = FieldType.Number, Required = true,
        Constraints = JsonSerializer.Deserialize<FieldConstraints>(constraints, ProtocolJson.Options),
    });

    static HostConnection Connection(IUiDispatcher ui) =>
        new(_ => Task.FromException<LocalChannel>(new InvalidOperationException("No channel in review tests.")), ui);

    static SessionCardViewModel Card(Question question, Deliveries deliveries) =>
        new(Make.Session(question: question), new CardContext
        {
            Deliveries = deliveries,
            Folds = new FoldStore(null),
            Capabilities = () => [Capabilities.SessionsRead, Capabilities.SessionsAnswer],
        });
}
