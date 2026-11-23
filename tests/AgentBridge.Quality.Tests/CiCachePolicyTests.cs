using Xunit;

namespace AgentBridge.Quality.Tests;

// ci.yml's cache rules (quality/README.md, "CI: speed, runners and what gates main", warm caches): only a push to main
// saves a cache, what ships never restores one, build output is never cached, and a job that can land on a reused
// ab-ci machine keeps NuGet's and MSBuild's state in RUNNER_TEMP for that job alone.
public sealed class CiCachePolicyTests
{
    const string MainPush = "github.event_name == 'push' && github.ref == 'refs/heads/main'";
    const string Restore = "actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9";
    const string Save = "actions/cache/save@55cc8345863c7cc4c66a329aec7e433d2d1c52a9";
    const string Guarded = "${{ vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository) && 'ab-ci' || matrix.os }}";
    const string PerJob = """
              - name: NuGet and MSBuild state for this job only
                shell: pwsh
                run: |
                  $t = $env:RUNNER_TEMP
                  "NUGET_PACKAGES=$t/nuget-packages", "NUGET_HTTP_CACHE_PATH=$t/nuget-http", "NUGET_PLUGINS_CACHE_PATH=$t/nuget-plugins", "DOTNET_CLI_HOME=$t/dotnet-home", "MSBUILDDISABLENODEREUSE=1" | Add-Content $env:GITHUB_ENV
                  git clean -ffdx
        """;
    const string Checkout = """
              - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1
                with:
                  persist-credentials: false
        """;
    const string Shutdown = """
              - name: Stop this job's build servers
                if: always()
                run: dotnet build-server shutdown
        """;

    static string Ci(string job, string steps, string runsOn = "windows-latest") => $$"""
        name: CI
        on: [push, pull_request]
        jobs:
          {{job}}:
            runs-on: {{runsOn}}
            steps:
        {{steps}}
        """;

    static string Cache(string uses, string path = "~/.nuget/packages", string condition = "") => $$"""
              - name: cache
                id: nuget
                {{(condition.Length > 0 ? $"if: {condition}" : "")}}
                uses: {{uses}}
                with:
                  path: {{path}}
                  key: nuget-key
        """;

    static IReadOnlyList<string> Check(string yaml, Dictionary<string, string>? actions = null)
        => CachePolicy.Violations(yaml, path => actions is not null && actions.TryGetValue(path, out var text) ? text : null);

    static string Composite(string steps) => "name: x\nruns:\n  using: composite\n  steps:\n" + steps.Replace("\n      ", "\n    ", StringComparison.Ordinal).Replace("      - ", "    - ", StringComparison.Ordinal);

    // B221 P1-1: local composite actions are read and held to the same rules, with the two documented exceptions.
    [Fact]
    public void A_local_composite_action_is_followed_and_held_to_the_same_rules()
    {
        var uses = "      - uses: ./.github/actions/x\n";
        Assert.NotEmpty(Check(Ci("product", uses), new() { ["./.github/actions/x"] = Composite(Cache("actions/cache@v4")) }));
        Assert.NotEmpty(Check(Ci("app", uses), new() { ["./.github/actions/x"] = Composite(Cache(Restore)) }));
        Assert.Empty(Check(Ci("product", uses), new() { ["./.github/actions/x"] = Composite(Cache(Restore)) }));
        // Unreadable: fail closed.
        Assert.NotEmpty(Check(Ci("product", uses)));
    }

    [Fact]
    public void The_two_documented_exceptions_are_named_and_only_they_pass()
    {
        // install-trivy: its restore is hash-checked before use, so a shipping job may take it.
        Assert.Empty(Check(Ci("app", "      - uses: ./.github/actions/install-trivy\n"), new() { ["./.github/actions/install-trivy"] = Composite(Cache(Restore)) }));
        // playwright-system-packages: its debs are verified against live signed indexes, so it saves on always().
        var apt = Composite(Cache(Save, condition: "always() && steps.packages.outputs.cache-ready == 'true'"));
        Assert.Empty(Check(Ci("pwa-ui", "      - uses: ./.github/actions/playwright-system-packages\n"), new() { ["./.github/actions/playwright-system-packages"] = apt }));
        Assert.NotEmpty(Check(Ci("pwa-ui", "      - uses: ./.github/actions/other\n"), new() { ["./.github/actions/other"] = apt }));
    }

    [Fact]
    public void A_reusable_workflow_job_fails_closed() => Assert.NotEmpty(Check("""
        name: CI
        on: [push]
        jobs:
          build:
            uses: ./.github/workflows/build.yml
        """));

    // B221 P2: the other ways a step can save or restore a cache.
    [Theory]
    [InlineData("actions/setup-dotnet@v5", "cache: true")]
    [InlineData("actions/setup-node@v5", "cache: npm")]
    [InlineData("buildjet/cache@v4", "path: x")]
    [InlineData("docker/build-push-action@v6", "cache-to: type=gha,mode=max")]
    public void Setup_caches_buildjet_and_docker_cache_to_count_as_saves(string uses, string with)
        => Assert.NotEmpty(Check(Ci("product", $"      - uses: {uses}\n        with:\n          {with}\n")));

    [Fact]
    public void A_docker_cache_to_on_main_pushes_only_is_allowed_but_cache_from_never_ships()
    {
        Assert.Empty(Check(Ci("product", $"      - if: {MainPush}\n        uses: docker/build-push-action@v6\n        with:\n          cache-to: type=gha\n")));
        Assert.NotEmpty(Check(Ci("publish", "      - uses: docker/build-push-action@v6\n        with:\n          cache-from: type=gha\n")));
    }

    [Fact]
    public void A_step_that_never_runs_and_text_in_a_comment_do_not_count()
    {
        Assert.Empty(Check(Ci("product", Cache("actions/cache@v4", condition: "false"))));
        var commented = PerJob.Replace("git clean -ffdx", "# git clean -ffdx", StringComparison.Ordinal) + "\n" + Shutdown;
        Assert.NotEmpty(Check(Ci("product", commented, Guarded)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("name: CI\non: [push]\n")]
    [InlineData("name: CI\non: [push]\njobs: [a, b]\n")]
    public void An_empty_file_or_no_jobs_map_fails_closed(string yaml) => Assert.NotEmpty(Check(yaml));

    [Fact]
    public void The_build_server_shutdown_is_the_last_step()
        => Assert.NotEmpty(Check(Ci("product", PerJob + "\n" + Shutdown + "\n      - run: echo after\n", Guarded)));

    // B221 P1-2: a runner that doesn't resolve to hosted labels may be ab-ci, so the per-job rules apply.
    [Fact]
    public void A_runner_read_from_an_unresolved_matrix_gets_the_ab_ci_rules()
        => Assert.NotEmpty(Check(Ci("product", Cache(Restore), "${{ matrix.os }}")));

    [Fact]
    public void A_restore_and_a_save_gated_on_main_pushes_are_allowed()
        => Assert.Empty(Check(Ci("product", Cache(Restore) + "\n" + Cache(Save, condition: MainPush + " && steps.nuget.outputs.cache-hit != 'true'"))));

    [Theory]
    [InlineData("")]
    [InlineData("github.ref == 'refs/heads/main'")]
    [InlineData("github.event_name == 'push'")]
    [InlineData("github.event_name == 'push' && github.ref == 'refs/heads/main' || github.event_name == 'pull_request'")]
    [InlineData("always()")]
    public void A_save_that_a_pull_request_could_reach_is_a_violation(string condition)
    {
        var one = Assert.Single(Check(Ci("product", Cache(Save, condition: condition))));
        Assert.Contains("job product", one);
    }

    [Fact]
    public void The_combined_cache_action_is_a_violation_since_it_saves_on_every_event()
        => Assert.Single(Check(Ci("product", Cache("actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9", condition: MainPush))));

    [Theory]
    [InlineData("app")]
    [InlineData("app-installer")]
    [InlineData("sign")]
    [InlineData("publish")]
    [InlineData("attest")]
    [InlineData("publish-gate")]
    public void What_ships_restores_from_the_feeds_never_from_a_cache(string job)
        => Assert.Contains(Check(Ci(job, Cache(Restore))), v => v.Contains($"job {job}", StringComparison.Ordinal));

    [Theory]
    [InlineData("src/AgentBridge.Host/obj")]
    [InlineData("|\n            ~/.nuget/packages\n            tests/x/bin/Release")]
    [InlineData("'**/bin'")]
    public void Build_output_is_never_cached(string path) => Assert.NotEmpty(Check(Ci("product", Cache(Restore, path: path))));

    [Fact]
    public void A_job_that_can_land_on_ab_ci_keeps_its_nuget_and_msbuild_state_in_runner_temp()
    {
        var good = PerJob + "\n" + Cache(Restore, path: "${{ runner.temp }}/nuget-packages") + "\n" + Shutdown;
        Assert.Empty(Check(Ci("product", good, Guarded)));

        Assert.Empty(Check(Ci("product", Checkout + "\n" + good, Guarded)));

        // No clean workspace: no git clean, or checkout's own clean turned off.
        Assert.NotEmpty(Check(Ci("product", good.Replace("git clean -ffdx", "", StringComparison.Ordinal), Guarded)));
        Assert.NotEmpty(Check(Ci("product", Checkout + "\n          clean: false\n" + good, Guarded)));
        // No tools' home under RUNNER_TEMP.
        Assert.NotEmpty(Check(Ci("product", good.Replace("\"DOTNET_CLI_HOME=$t/dotnet-home\", ", "", StringComparison.Ordinal), Guarded)));
        // No per-job folders, the machine's own folder cached, the folders set after the restore, or no shutdown.
        Assert.NotEmpty(Check(Ci("product", Cache(Restore, path: "${{ runner.temp }}/nuget-packages") + "\n" + Shutdown, Guarded)));
        Assert.NotEmpty(Check(Ci("product", PerJob + "\n" + Cache(Restore) + "\n" + Shutdown, Guarded)));
        Assert.NotEmpty(Check(Ci("product", Cache(Restore, path: "${{ runner.temp }}/nuget-packages") + "\n" + PerJob + "\n" + Shutdown, Guarded)));
        Assert.NotEmpty(Check(Ci("product", PerJob + "\n" + Cache(Restore, path: "${{ runner.temp }}/nuget-packages"), Guarded)));
    }

    [Fact]
    public void A_hosted_job_needs_no_per_job_folders() => Assert.Empty(Check(Ci("product", Cache(Restore))));

    [Fact]
    public void The_repositorys_ci_yml_passes()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }

        Assert.Empty(CachePolicy.Violations(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")),
            path => File.Exists(Path.Combine(root, path, "action.yml")) ? File.ReadAllText(Path.Combine(root, path, "action.yml")) : null));
    }
}
