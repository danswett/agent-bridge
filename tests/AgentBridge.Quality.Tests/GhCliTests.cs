using System.Text.Json;
using Xunit;

namespace AgentBridge.Quality.Tests;

// The merge rule reads GitHub through gh. A truncated or empty answer (gh's own "unexpected end of JSON input", or an empty body) was one flaky
// read that errored every pull request's merge-rule; it is retried once, and when it still fails the message says what gh said and how often it was asked.
public sealed class GhCliTests
{
    sealed class Script(params (int Exit, string Out, string Err)[] answers)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public (int, string, string) Exec(IReadOnlyList<string> args, string? token)
        {
            Calls.Add(args);
            return answers[Math.Min(Calls.Count - 1, answers.Length - 1)];
        }
    }

    const string Jobs = """{"total_count":1,"jobs":[]}""";

    [Fact]
    public void A_truncated_answer_from_gh_is_asked_for_again_once_and_the_second_answer_is_used()
    {
        var script = new Script((1, "", "unexpected end of JSON input"), (0, Jobs, ""));

        using var doc = new GhCli(script.Exec).Rest("repos/o/r/actions/runs/1/jobs");

        Assert.Equal(1, doc.RootElement.GetProperty("total_count").GetInt32());
        Assert.Equal(2, script.Calls.Count);
    }

    [Fact]
    public void An_empty_body_with_exit_0_is_asked_for_again_once()
    {
        var script = new Script((0, "", ""), (0, Jobs, ""));

        using var doc = new GhCli(script.Exec).Rest("repos/o/r/actions/runs/1/jobs");

        Assert.Equal(2, script.Calls.Count);
        Assert.Equal(1, doc.RootElement.GetProperty("total_count").GetInt32());
    }

    [Fact]
    public void When_both_answers_fail_the_error_carries_gh_s_stderr_its_status_and_how_often_it_was_asked()
    {
        var script = new Script((1, "", "gh: Bad Gateway (HTTP 502): unexpected end of JSON input"), (1, "", "gh: Bad Gateway (HTTP 502): unexpected end of JSON input"));

        var e = Assert.Throws<InvalidOperationException>(() => new GhCli(script.Exec).Rest("repos/o/r/actions/runs/1/jobs"));

        Assert.Contains("HTTP 502", e.Message, StringComparison.Ordinal);
        Assert.Contains("after 2 attempts", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, script.Calls.Count);
    }

    [Fact]
    public void A_body_that_is_still_empty_after_the_retry_is_an_error_naming_the_empty_response_not_a_bare_json_error()
    {
        var script = new Script((0, "", ""), (0, "  ", ""));

        var e = Assert.Throws<InvalidOperationException>(() => new GhCli(script.Exec).Rest("repos/o/r/actions/runs/1/jobs"));

        Assert.Contains("empty response", e.Message, StringComparison.Ordinal);
        Assert.Contains("after 2 attempts", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_5xx_that_is_not_a_cut_short_answer_is_asked_once_and_says_what_gh_said()
    {
        var script = new Script((1, "", "gh: Bad Gateway (HTTP 502)"));

        var e = Assert.Throws<InvalidOperationException>(() => new GhCli(script.Exec).Rest("repos/o/r/actions/runs/1/jobs"));

        Assert.Single(script.Calls);
        Assert.Contains("HTTP 502", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_404_is_an_answer_not_a_flake_so_it_is_asked_once_and_the_status_is_in_the_message()
    {
        var script = new Script((1, "", "gh: Not Found (HTTP 404)"));

        var e = Assert.Throws<InvalidOperationException>(() => new GhCli(script.Exec).Rest("repos/o/r/actions/runs/1/jobs"));

        Assert.Single(script.Calls);
        Assert.Contains("HTTP 404", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("attempts", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_post_is_never_asked_for_again_whatever_gh_said()
    {
        var script = new Script((1, "", "unexpected end of JSON input"), (0, "{}", ""));

        Assert.Throws<InvalidOperationException>(() => new GhCli(script.Exec).PostStatus("o/r", "abc", "success", "merge-rule", "ok", null));

        Assert.Single(script.Calls);
    }

    [Fact]
    public void A_log_that_is_empty_is_returned_as_it_is_not_an_error_or_a_retry()
    {
        var script = new Script((0, "", ""));

        Assert.Equal("", new GhCli(script.Exec).RestText("repos/o/r/actions/jobs/1/logs"));
        Assert.Single(script.Calls);
    }

    [Fact]
    public void A_graphql_answer_that_is_truncated_is_asked_for_again_once()
    {
        var script = new Script((1, "", "unexpected end of JSON input"), (0, """{"data":{}}""", ""));

        using var doc = new GhCli(script.Exec).GraphQL("query { viewer { login } }", new Dictionary<string, object>());

        Assert.Equal(2, script.Calls.Count);
        Assert.True(doc.RootElement.TryGetProperty("data", out _));
    }
}
