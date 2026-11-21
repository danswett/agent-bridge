using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class NativeFormContractTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("count")]
    [InlineData("extras")]
    public async Task Changed_limits_rebuild_the_native_five_field_form_and_refuse_the_old_values(string name)
    {
        var ui = new LockedDispatcher();
        await using var connection = UnstartedConnection(ui);
        var deliveries = new Deliveries(connection, ui, new FakeTimeProvider(Make.T0));
        var question = Row17();
        var card = Card(question, deliveries);
        try
        {
            var old = card.Question!;
            Fill(old);
            AssertNamedAnswer(old);
            var changed = question with
            {
                Fields = [.. question.Fields.Select(f => f.Name != name ? f : f with
                {
                    Constraints = name switch
                    {
                        "name" => new FieldConstraints { MaxLength = 2 },
                        "count" => new FieldConstraints { Maximum = Json(2) },
                        _ => new FieldConstraints { MaxItems = 1 },
                    },
                })],
            };

            card.Update(Make.Session(question: changed));

            var current = card.Question!;
            Assert.NotSame(old, current);
            Assert.False(old.CanAnswer);
            Assert.Contains("name: Zo\u00eb \U0001f680", card.OrphanText, StringComparison.Ordinal);
            Assert.All(current.Fields, f =>
            {
                Assert.Empty(f.Text);
                Assert.Null(f.BoolValue);
                Assert.DoesNotContain(f.Options, o => o.IsSelected);
            });
            Fill(current);
            current.Send();
            Assert.Contains($"\"{name}\"", current.Error, StringComparison.Ordinal);
            Assert.True(current.Fields.Single(f => f.Name == name).Invalid);
            Assert.Null(deliveries.Get(current.Key));
        }
        finally { card.Detach(); }
    }

    [Fact]
    public async Task Rehydrating_equal_limits_keeps_the_same_native_form_and_all_five_drafts()
    {
        var ui = new LockedDispatcher();
        await using var connection = UnstartedConnection(ui);
        var deliveries = new Deliveries(connection, ui, new FakeTimeProvider(Make.T0));
        var question = Row17();
        var card = Card(question, deliveries);
        try
        {
            var original = card.Question!;
            Fill(original);
            var refreshed = JsonSerializer.Deserialize<Question>(JsonSerializer.Serialize(question, ProtocolJson.Options), ProtocolJson.Options)!;
            Assert.NotSame(question.Fields[3].Constraints, refreshed.Fields[3].Constraints);

            card.Update(Make.Session(question: refreshed));

            Assert.Same(original, card.Question);
            Assert.False(card.HasOrphan);
            AssertNamedAnswer(card.Question!);
        }
        finally { card.Detach(); }
    }

    [Fact]
    public void Adding_or_removing_limits_changes_the_native_form_contract()
    {
        var constrained = Row17();
        var unbounded = constrained with
        {
            Fields = [.. constrained.Fields.Select(f => f with { Constraints = null })],
        };

        Assert.False(QuestionViewModel.SaysTheSame(constrained, unbounded));
        Assert.False(QuestionViewModel.SaysTheSame(unbounded, constrained));
    }

    static HostConnection UnstartedConnection(IUiDispatcher ui) =>
        new(_ => Task.FromException<LocalChannel>(new InvalidOperationException("A form-model test must not open a channel.")), ui);

    static SessionCardViewModel Card(Question question, Deliveries deliveries) =>
        new(Make.Session(question: question), new CardContext
        {
            Deliveries = deliveries,
            Folds = new FoldStore(null),
            Capabilities = () => [Capabilities.SessionsRead, Capabilities.SessionsAnswer],
        });

    static Question Row17() => Make.Form(
        new Field { Name = "colour", Type = FieldType.Choice, Required = true, Options = [new("red"), new("green"), new("blue")] },
        new Field { Name = "confirm", Type = FieldType.Boolean, Required = true },
        new Field { Name = "name", Type = FieldType.Text, Required = true, Constraints = new() { MaxLength = 20 } },
        new Field { Name = "count", Type = FieldType.Number, Required = true, Constraints = new() { Maximum = Json(5) } },
        new Field { Name = "extras", Type = FieldType.Multi, Required = true, Options = [new("x"), new("y"), new("z")], Constraints = new() { MaxItems = 3 } });

    static void Fill(QuestionViewModel form)
    {
        form.Fields.Single(f => f.Name == "colour").Options.Single(o => o.Value == "green").IsSelected = true;
        form.Fields.Single(f => f.Name == "confirm").BoolValue = true;
        form.Fields.Single(f => f.Name == "name").Text = "Zo\u00eb \U0001f680";
        form.Fields.Single(f => f.Name == "count").Text = "3";
        var extras = form.Fields.Single(f => f.Name == "extras");
        extras.Options.Single(o => o.Value == "z").IsSelected = true;
        extras.Options.Single(o => o.Value == "x").IsSelected = true;
    }

    static void AssertNamedAnswer(QuestionViewModel form)
    {
        Assert.False(form.Tap);
        var (answer, problem, _) = form.Build();
        Assert.Null(problem);
        Assert.Null(Answers.Check(form.Question, answer));
        var fields = JsonSerializer.SerializeToElement(answer, ProtocolJson.Options).GetProperty("fields");
        Assert.Equal(["colour", "confirm", "name", "count", "extras"], fields.EnumerateObject().Select(f => f.Name));
        Assert.Equal("green", fields.GetProperty("colour").GetString());
        Assert.True(fields.GetProperty("confirm").GetBoolean());
        Assert.Equal("Zo\u00eb \U0001f680", fields.GetProperty("name").GetString());
        Assert.Equal(3, fields.GetProperty("count").GetInt32());
        Assert.Equal(["x", "z"], fields.GetProperty("extras").EnumerateArray().Select(v => v.GetString()));
    }

    static JsonElement Json(int value) => JsonSerializer.SerializeToElement(value);
}
