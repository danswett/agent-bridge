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

    static IReadOnlyList<string> Check(string yaml) => CachePolicy.Violations(yaml);

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

        Assert.Empty(Check(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))));
    }
}
