using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Xunit;

// Text the tolerant read rewrites (half a surrogate pair reads as U+FFFD) is for display only.
// An approval is identified and escape-checked from its text, so commands differing only in which
// lone surrogate they hold, or in holding U+FFFD, shared an id and lost their escaped flag
// (Engineering's review of #921). Each is now held at the machine under an id of its own.
public sealed class LossyApprovalTests
{
    static readonly string[] Spellings = ["\\uD800", "\\uD801", "\\uFFFD"];

    [Fact]
    public void Codex_commands_that_read_alike_once_rewritten_are_held_at_the_machine_under_ids_of_their_own()
    {
        using var f = new Fixture();
        var registration = Path.Combine(f.Root, "reg.json");
        var questions = new List<Question>();
        foreach (var spelling in Spellings)
        {
            var path = ApprovalMarkerRead.PathFor(registration, "s1");
            File.WriteAllText(path, $$"""{"SessionId":"s1","DecisionId":"d1","Tool":"Bash","Command":"echo {{spelling}}","Question":"Needs approval"}""");
            var marker = ApprovalMarkerRead.Read(registration, "s1");
            var q = QuestionMapper.CodexApproval(marker.DecisionId!, marker.Tool, marker.Command, marker.CommandCut, marker.Question!, Fixture.T0, atMachine: false, lossy: marker.Lossy);
            Assert.True(q.AnswerAtMachine);
            Assert.Equal(q.Id, CodexApprovalId.Of(marker));
            questions.Add(q);
        }
        Assert.Equal(Spellings.Length, questions.Select(q => q.Id).Distinct().Count());
    }

    [Fact]
    public void A_Codex_marker_without_such_text_is_as_it_was()
    {
        using var f = new Fixture();
        var registration = Path.Combine(f.Root, "reg.json");
        File.WriteAllText(ApprovalMarkerRead.PathFor(registration, "s1"), """{"SessionId":"s1","DecisionId":"d1","Tool":"Bash","Command":"echo ok","Question":"Needs approval"}""");
        var marker = ApprovalMarkerRead.Read(registration, "s1");

        Assert.Null(marker.Lossy);
        Assert.Null(QuestionMapper.CodexApproval("d1", marker.Tool, marker.Command, false, marker.Question!, Fixture.T0, atMachine: false).AnswerAtMachine);
    }

    [Fact]
    public void Copilot_permission_prompts_that_read_alike_once_rewritten_are_held_at_the_machine_under_ids_of_their_own()
    {
        var prompts = Spellings.Select(spelling =>
        {
            var prompt = Copilot.Prompts["commands"].DeepClone().AsObject();
            prompt["fullCommandText"] = "echo MARK";
            var line = Copilot.Permission("r1", "c1", prompt, 1).Replace("echo MARK", "echo " + spelling);
            using var doc = JsonDocument.Parse(line);
            return CopilotPermission.Describe(doc.RootElement.GetProperty("data").Clone())!;
        }).ToList();

        Assert.All(prompts, p => Assert.NotNull(p.AtMachine));
        Assert.Equal(Spellings.Length, prompts.Select(p => p.QuestionId).Distinct().Count());
    }

    [Fact]
    public void Copilot_forms_with_a_lone_surrogate_in_a_string_typed_schema_are_answered_at_the_machine()
    {
        using var copilot = JsonDocument.Parse("""{ "message": "Pick", "requestedSchema": "{\"title\":\"a\\uD800\",\"properties\":{\"f\":{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}}}" }""");

        Assert.True(QuestionMapper.FromCopilotAskUser("c1", copilot.RootElement, Fixture.T0).AnswerAtMachine);
    }

    [Fact]
    public void Forms_with_such_text_in_a_name_or_an_option_are_answered_at_the_machine()
    {
        using var copilot = JsonDocument.Parse("""{ "message": "Pick", "requestedSchema": { "properties": { "f": { "type": "string", "enum": ["a\uD800", "b"] } } } }""");
        using var claude = JsonDocument.Parse("""{ "questions": [ { "question": "Which?", "multiSelect": false, "options": ["a\uD800", "b"] } ] }""");
        using var literal = JsonDocument.Parse("""{ "questions": [ { "question": "Which?", "multiSelect": false, "options": ["a\uFFFD", "b"] } ] }""");

        Assert.True(QuestionMapper.FromCopilotAskUser("c1", copilot.RootElement, Fixture.T0).AnswerAtMachine);
        Assert.True(QuestionMapper.FromClaudeAskUserQuestion("c2", claude.RootElement, Fixture.T0).AnswerAtMachine);
        Assert.True(QuestionMapper.FromClaudeAskUserQuestion("c3", literal.RootElement, Fixture.T0).AnswerAtMachine);
    }
}
