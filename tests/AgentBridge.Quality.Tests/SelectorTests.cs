using System.Text.Json;

namespace AgentBridge.Quality.Tests;

public class SelectorTests
{
    [Fact]
    public void A_change_to_a_library_selects_every_test_project_that_reaches_it_and_no_other()
    {
        var selection = Fixture.Selector().Select(["src/HostCore/Parser.cs"], ring: 0);

        Assert.Equal(["protocol", "host", "homeassistant"], Fixture.Ids(selection));
        Assert.Equal(["src/HostCore/Parser.cs"], Fixture.ReasonFor(selection, "host", "project tests/Host.Tests reaches src/HostCore").Files);
        Assert.Equal("affected", selection.Mode);
    }

    [Fact]
    public void Both_suites_on_a_shared_test_project_are_selected_when_it_is_reached()
    {
        var selection = Fixture.Selector().Select(["src/HomeAssistant/Connector.cs"], ring: 1);

        Assert.Contains("homeassistant", Fixture.Ids(selection));
        Assert.Contains("ha-integration", Fixture.Ids(selection));
    }

    [Fact]
    public void A_file_a_test_project_links_from_outside_its_folder_selects_that_project()
    {
        var selection = Fixture.Selector().Select(["tests/fixtures/shared/subagents.jsonl"], ring: 0);

        Assert.Equal(["protocol", "host"], Fixture.Ids(selection));
        Assert.Empty(selection.Unclaimed);
    }

    [Fact]
    public void A_path_matching_a_suites_triggers_selects_that_suite_in_the_requested_ring_only()
    {
        var selector = Fixture.Selector();

        var ring0 = selector.Select(["pwa/app.css"], ring: 0);
        var ring1 = selector.Select(["pwa/app.css"], ring: 1);

        Assert.Equal(["protocol", "pwa-unit"], Fixture.Ids(ring0));
        Assert.Equal(["pwa/app.css"], Fixture.ReasonFor(ring0, "pwa-unit", "triggers pwa/**").Files);
        Assert.Equal(["protocol", "pwa-unit", "pwa-ui", "codeql"], Fixture.Ids(ring1));
    }

    [Fact]
    public void A_global_trigger_selects_every_suite_in_the_ring()
    {
        var selection = Fixture.Selector().Select([".github/workflows/ci.yml", "pwa/app.css"], ring: 1);

        Assert.Equal(["protocol", "transport", "host", "homeassistant", "ha-integration", "docs-links", "pwa-unit", "pwa-ui", "apple-core", "infra", "codeql"], Fixture.Ids(selection));
        Assert.Equal("full", selection.Mode);
        Assert.Equal([".github/workflows/ci.yml"], Fixture.ReasonFor(selection, "transport", "global_triggers .github/workflows/**").Files);
    }

    [Fact]
    public void A_docs_only_change_selects_only_what_its_triggers_name_not_the_sentinels()
    {
        var selector = Fixture.Selector();

        var ring1 = selector.Select(["README.md", "docs/status.md", "docs/design/mock.gif"], ring: 1);
        var ring0 = selector.Select(["README.md"], ring: 0);

        Assert.Equal(["docs-links"], Fixture.Ids(ring1));
        Assert.Equal(["README.md", "docs/status.md"], Fixture.ReasonFor(ring1, "docs-links", "triggers **/*.md").Files);
        Assert.Equal("docs-only", ring1.Mode);
        Assert.Empty(ring1.Unclaimed);
        Assert.Empty(ring0.Suites);
    }

    [Fact]
    public void Markdown_below_the_root_is_not_docs_only_unless_a_rule_says_so()
    {
        var selection = Fixture.Selector().Select(["pwa/README.md"], ring: 0);

        Assert.Equal("affected", selection.Mode);
        Assert.Contains("pwa-unit", Fixture.Ids(selection));
    }

    [Fact]
    public void A_path_no_rule_claims_selects_the_full_ring_and_names_the_path()
    {
        var selection = Fixture.Selector().Select(["src/Host/Program.cs", "newfolder/thing.txt"], ring: 0);

        Assert.Equal(["newfolder/thing.txt"], selection.Unclaimed);
        Assert.Equal("full", selection.Mode);
        Assert.Equal(["protocol", "transport", "host", "homeassistant", "pwa-unit", "apple-core"], Fixture.Ids(selection));
        Assert.Equal(["newfolder/thing.txt"], Fixture.ReasonFor(selection, "apple-core", Selector.FailSafe).Files);
    }

    [Fact]
    public void A_path_claimed_only_by_a_later_rings_suite_is_known_not_a_fail_safe()
    {
        var selection = Fixture.Selector().Select(["infra/main.bicep"], ring: 0);

        Assert.Empty(selection.Unclaimed);
        Assert.Equal(["protocol"], Fixture.Ids(selection));
    }

    [Fact]
    public void Sentinels_run_at_their_ring_with_any_change_that_isnt_docs_only()
    {
        var selection = Fixture.Selector().Select(["src/Relay/Program.cs"], ring: 1);

        Assert.Equal(["protocol", "transport", "codeql"], Fixture.Ids(selection));
        Assert.Empty(Fixture.ReasonFor(selection, "codeql", Selector.Sentinel).Files);
    }

    [Fact]
    public void A_clean_tree_still_runs_the_sentinels_of_its_ring()
    {
        var selection = Fixture.Selector().Select([], ring: 0);

        Assert.Equal(["protocol"], Fixture.Ids(selection));
        Assert.Equal("affected", selection.Mode);
    }

    [Fact]
    public void Ring_two_runs_everything_up_to_it_whatever_changed()
    {
        var selection = Fixture.Selector().Select(["README.md"], ring: 2);

        Assert.Equal(12, selection.Suites.Count);
        Assert.Equal("everything", selection.Mode);
    }

    [Fact]
    public void The_platform_filter_keeps_browser_suites_and_says_why_others_were_left_out()
    {
        var selection = Fixture.Selector().Select(["pwa/app.css", "apple/Sources/App.swift"], ring: 0, platform: "win-x64");

        Assert.Equal(["protocol", "pwa-unit"], Fixture.Ids(selection));
        var skipped = Assert.Single(selection.Skipped);
        Assert.Equal("apple-core", skipped.Suite.Id);
        Assert.Equal("runs on macos-arm64, not win-x64", skipped.Why);
    }

    [Fact]
    public void Suites_asked_for_by_name_run_whatever_changed_and_an_unknown_name_is_refused()
    {
        var selector = Fixture.Selector();

        var selection = selector.Select([], ring: 0, named: ["pwa-ui"]);

        Assert.Equal(["pwa-ui"], Fixture.Ids(selection));
        Assert.Throws<ArgumentException>(() => selector.Select([], ring: 0, named: ["nope"]));
    }

    [Fact]
    public void Explain_prints_each_suite_with_the_rule_and_files_that_pulled_it_in()
    {
        var selection = Fixture.Selector().Select(["src/HostCore/Parser.cs", "src/HostCore/Reader.cs"], ring: 0, platform: "win-x64");
        var output = new StringWriter();

        Program.Explain(selection, "the files given", output);

        var text = output.ToString();
        Assert.Contains("Ring 0, on win-x64. 2 changed file(s) from the files given. Mode: affected.", text);
        Assert.Matches(@"host +0 +local .*project tests/Host.Tests reaches src/HostCore: src/HostCore/Parser.cs, src/HostCore/Reader.cs", text);
        Assert.Matches(@"protocol +0 +local .*always \(sentinel\)", text);
        Assert.Contains("win-x64: protocol, host, homeassistant", text);
    }

    [Fact]
    public void Explain_names_a_whole_ring_cause_once_and_points_each_suite_at_it()
    {
        var selection = Fixture.Selector().Select(["newfolder/thing.txt"], ring: 0);
        var output = new StringWriter();

        Program.Explain(selection, "the files given", output);

        var text = output.ToString();
        Assert.Contains($"The whole ring runs, by {Selector.FailSafe}: newfolder/thing.txt", text);
        Assert.Matches(@"transport +0 +local .*the whole ring \(above\)", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "newfolder/thing.txt"));
    }

    [Fact]
    public void An_option_without_its_value_or_an_unknown_option_is_refused_and_repeats_accumulate()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseOptions(["--ring"]));
        Assert.Throws<ArgumentException>(() => Program.ParseOptions(["--platform", "--explain"]));
        Assert.Throws<ArgumentException>(() => Program.ParseOptions(["--rings", "1"]));

        var options = Program.ParseOptions(["--files", "a.cs", "--explain", "--files", "b.cs"]);

        Assert.Equal("a.cs,b.cs", options["files"]);
        Assert.True(options.ContainsKey("explain"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("5")]
    [InlineData("one")]
    public void A_ring_outside_zero_to_four_is_refused_rather_than_selecting_nothing(string ring) =>
        Assert.Throws<ArgumentException>(() => Program.ParseRing(ring));

    [Fact]
    public void An_unknown_platform_is_refused_rather_than_skipping_every_suite()
    {
        Assert.Throws<ArgumentException>(() => Program.ParsePlatform("windows-x64"));
        Assert.Equal("linux-arm64", Program.ParsePlatform("linux-arm64"));
        Assert.Null(Program.ParsePlatform("any"));
    }

    [Fact]
    public void Deleting_a_projects_csproj_still_reaches_the_tests_that_reference_it()
    {
        // src/HostCore's .csproj is gone from disk, but Host and HomeAssistant still reference it.
        var graph = new ProjectGraph(Fixture.Graph().Projects.Where(p => p.Folder != "src/HostCore"));
        var selector = new Selector(Fixture.Catalog(), graph);

        var selection = selector.Select(["src/HostCore/HostCore.csproj"], ring: 0);

        Assert.Equal(["protocol", "host", "homeassistant"], Fixture.Ids(selection));
        Assert.Empty(selection.Unclaimed);
    }

    [Fact]
    public void The_json_lists_each_suite_with_its_reasons_and_the_os_matrix()
    {
        var selection = Fixture.Selector().Select(["src/Relay/Program.cs"], ring: 1);

        using var json = JsonDocument.Parse(Program.ToJson(selection, "test"));

        var transport = json.RootElement.GetProperty("suites").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "transport");
        var reason = transport.GetProperty("reasons")[0];
        Assert.Equal("project tests/Transport.Tests reaches src/Relay", reason.GetProperty("rule").GetString());
        Assert.Equal("src/Relay/Program.cs", reason.GetProperty("files")[0].GetString());
        var linux = json.RootElement.GetProperty("matrix").GetProperty("linux-x64").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["protocol", "transport", "codeql"], linux);
    }
}
