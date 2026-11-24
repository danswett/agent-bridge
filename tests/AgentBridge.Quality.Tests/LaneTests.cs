namespace AgentBridge.Quality.Tests;

/// <summary>
/// The two lanes (S2, #4104): ci.yml is the FAST lane, full.yml the FULL lane that never gates a merge. A suite says which in
/// <c>lane</c> (fast when absent), and the validators read both workflows.
/// </summary>
public class LaneTests
{
    static string Suite(string id, string where, string? lane = null) => $"""
          - id: {id}
            validation_plan: native
            kind: ui
            ring: 1
            select: always
            platforms: [browser]
            runner: hosted
            command: see the workflow
            where: {where}
            status: exists
        {(lane is null ? "" : "    lane: " + lane)}
        """ + "\n";

    static Catalog Catalog(params string[] suites) => AgentBridge.Quality.Catalog.Parse("suites:\n" + string.Concat(suites));

    static IEnumerable<string> Lane(Catalog catalog) => catalog.Validate(_ => 1).Where(p => p.Kind == "lane").Select(p => p.Message);

    [Fact]
    public void A_suite_without_a_lane_is_fast_and_an_unknown_lane_is_reported()
    {
        var catalog = Catalog(Suite("a", "ci.yml light"), Suite("b", "ci.yml light", "fast"), Suite("c", "ci.yml light", "slow"));

        Assert.Equal(["fast", "fast", "slow"], catalog.Suites.Select(s => s.Lane ?? "fast"));
        Assert.Equal(["suite c has lane 'slow', not one of fast, full, so it counts as fast"], Lane(catalog));
    }

    [Fact]
    public void A_full_suite_runs_in_full_yml_only_and_a_fast_suite_never_does()
    {
        var catalog = Catalog(
            Suite("full-ok", "full.yml spikes, full.yml pwa-ui", "full"),
            Suite("full-in-ci", "ci.yml spikes", "full"),
            Suite("full-both", "ci.yml light, full.yml app-pack", "full"),
            Suite("full-nowhere", "perf.yml perf", "full"),
            Suite("fast-in-full", "full.yml app-pack"),
            Suite("fast-ok", "ci.yml light"),
            Suite("fast-other", "perf.yml perf", "fast"));

        Assert.Equal([
            "suite full-in-ci is lane full, but its where names ci.yml: the FULL lane's jobs are full.yml's",
            "suite full-both is lane full, but its where names ci.yml: the FULL lane's jobs are full.yml's",
            "suite full-nowhere is lane full, but its where names no full.yml job",
            "suite fast-in-full is lane fast (the default), but its where names full.yml: mark it lane: full",
        ], Lane(catalog));
    }

    // A full.yml that is as it should be, and one defect at a time.
    const string GoodRaw = """
        name: Full
        on:
          workflow_run:
            workflows: [CI]
            types: [completed]
          schedule:
            - cron: '17 * * * *'
          workflow_dispatch:
        jobs:
          spikes:
            runs-on: ubuntu-latest
            steps: [{ run: echo }]
          app-pack:
            runs-on: windows-latest
            steps: [{ run: echo }]
          full-gate:
            needs: [spikes, app-pack]
            if: always()
            runs-on: ubuntu-latest
            steps:
              - env:
                  NEEDS: ${{ toJSON(needs) }}
                run: echo "$NEEDS"
        """;

    // A raw string takes the file's line endings, and these tests edit it by its LF text: a Windows checkout has CRLF.
    static readonly string Good = GoodRaw.ReplaceLineEndings("\n");

    static IEnumerable<string> FullLane(string yaml) =>
        AgentBridge.Quality.FullLane.Check(Catalog(Suite("a", "full.yml spikes, full.yml app-pack, full.yml full-gate", "full")), yaml).Select(p => $"{p.Kind}: {p.Message}");

    [Fact]
    public void A_full_workflow_with_its_gate_and_no_merge_trigger_passes()
    {
        Assert.Empty(FullLane(Good));
    }

    [Theory]
    [InlineData("  workflow_dispatch:\n", "  workflow_dispatch:\n  pull_request:\n", "pull_request")]
    [InlineData("  workflow_dispatch:\n", "  workflow_dispatch:\n  push:\n    branches: [main]\n", "push")]
    [InlineData("  workflow_dispatch:\n", "  workflow_dispatch:\n  merge_group:\n", "merge_group")]
    [InlineData("  workflow_dispatch:\n", "  workflow_dispatch:\n  pull_request_target:\n", "pull_request_target")]
    public void A_full_workflow_that_can_run_for_a_pull_request_or_a_push_is_refused_since_it_never_gates_a_merge(string from, string to, string trigger)
    {
        var problems = FullLane(Good.Replace(from, to));

        Assert.Equal([$"full-lane: full.yml triggers on {trigger}, but the FULL lane runs only on workflow_run, schedule and workflow_dispatch"], problems);
    }

    [Fact]
    public void The_gate_must_exist_need_every_other_job_run_always_and_read_the_needs()
    {
        Assert.Equal(["full-lane: full.yml has no full-gate job"],
            FullLane(Good.Replace("full-gate:", "the-gate:")));
        Assert.Equal(["full-lane: full.yml full-gate doesn't need app-pack"],
            FullLane(Good.Replace("needs: [spikes, app-pack]", "needs: [spikes]")));
        Assert.Equal(["full-lane: full.yml full-gate's if: isn't always(), so a failed or skipped job would skip the gate instead of failing it"],
            FullLane(Good.Replace("if: always()", "if: success()")));
        Assert.Equal(["full-lane: full.yml full-gate reads no toJSON(needs), so it cannot tell a skipped job from a passed one"],
            FullLane(Good.Replace("${{ toJSON(needs) }}", "x")));
    }

    [Fact]
    public void A_full_workflow_with_no_jobs_is_refused()
    {
        Assert.Equal(["full-lane: full.yml has no jobs"], FullLane("name: Full\non: workflow_dispatch\n"));
    }

    [Fact]
    public void Completeness_reads_full_yml_too_so_a_job_no_suite_names_and_a_full_job_missing_from_it_are_reported()
    {
        var catalog = Catalog(Suite("a", "full.yml spikes, full.yml app-pack, full.yml full-gate", "full"));
        var inputs = (string yaml, IReadOnlyList<string> jobs) => new Completeness.Inputs(
            [], new Dictionary<string, IReadOnlyList<string>> { ["full.yml"] = jobs }, [], FullWorkflow: yaml);
        var graph = new ProjectGraph([]);

        var clean = Completeness.Check(catalog, graph, inputs(Good, ["spikes", "app-pack", "full-gate"]))
            .Where(p => p.Kind is "workflow-job" or "full-lane" or "lane").Select(p => p.Message);
        var stray = Completeness.Check(catalog, graph, inputs(Good, ["spikes", "app-pack", "full-gate", "extra"]))
            .Where(p => p.Kind is "workflow-job" or "full-lane" or "lane").Select(p => p.Message);
        var missing = Completeness.Check(catalog, graph, inputs(Good, ["spikes", "full-gate"]))
            .Where(p => p.Kind is "workflow-job" or "full-lane" or "lane").Select(p => p.Message);

        Assert.Empty(clean);
        Assert.Equal(["full.yml job extra isn't named in any suite's where:"], stray);
        Assert.Equal(["suite a says it runs in full.yml job app-pack, which doesn't exist"], missing);
    }
}
