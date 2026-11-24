using Xunit;

namespace AgentBridge.Quality.Tests;

// The capacity check before AB_CI_SELF_HOSTED is set to true (quality/README.md, "CI: speed, runners and what gates
// main", capacity first): the ab-ci pool has runners online and its jobs start within about a minute.
public sealed class AbCiCapacityTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    static string Runner(string name, bool online = true, bool busy = false, string labels = "\"self-hosted\",\"windows\",\"x64\",\"ab-ci\"")
        => $$"""{"name":"{{name}}","status":"{{(online ? "online" : "offline")}}","busy":{{(busy ? "true" : "false")}},"labels":[{{string.Join(",", labels.Split(',').Select(l => $"{{\"name\":{l}}}"))}}]}""";

    static string Runners(params string[] runners) => $$"""{"total_count":{{runners.Length}},"runners":[{{string.Join(",", runners)}}]}""";

    static string Job(int secondsQueued, string status = "queued", string labels = "\"self-hosted\",\"windows\",\"x64\",\"ab-ci\"")
        => $$"""{"id":1,"name":"product (windows-latest)","status":"{{status}}","created_at":"{{Now.AddSeconds(-secondsQueued).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}","labels":[{{labels}}]}""";

    static string Jobs(params string[] jobs) => $$"""{"jobs":[{{string.Join(",", jobs)}}]}""";

    static AbCiCapacity.Result Check(string runners, string jobs, int maxQueued = 0, int maxWaitSeconds = 60, int minOnline = 1)
        => AbCiCapacity.Evaluate(runners, [jobs], Now, maxQueued, TimeSpan.FromSeconds(maxWaitSeconds), minOnline);

    [Fact]
    public void An_idle_online_pool_with_nothing_queued_passes()
    {
        var result = Check(Runners(Runner("a"), Runner("b", busy: true)), Jobs());

        Assert.True(result.Ok);
        Assert.Equal((2, 1, 1, 0), (result.Online, result.Busy, result.Idle, result.Queued));
    }

    [Fact]
    public void No_online_ab_ci_runner_fails_even_with_nothing_queued()
    {
        // Offline, and online but without the ab-ci label, don't count.
        var result = Check(Runners(Runner("a", online: false), Runner("b", labels: "\"self-hosted\",\"windows\"")), Jobs());

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.Contains("online", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 0, 60, false)]   // one queued, none allowed
    [InlineData(1, 1, 60, true)]    // one allowed, starting soon
    [InlineData(2, 1, 60, false)]
    public void More_queued_ab_ci_jobs_than_allowed_fails(int queued, int maxQueued, int maxWait, bool ok)
    {
        var jobs = Jobs([.. Enumerable.Range(0, queued).Select(_ => Job(10))]);

        Assert.Equal(ok, Check(Runners(Runner("a", busy: true)), jobs, maxQueued, maxWait).Ok);
    }

    [Fact]
    public void A_job_waiting_longer_than_the_limit_fails_even_within_the_count()
    {
        var result = Check(Runners(Runner("a", busy: true)), Jobs(Job(95)), maxQueued: 5);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.Contains("95 s", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_queued_jobs_for_the_pool_count()
    {
        // In progress, or queued for a hosted runner: not the pool's queue.
        var jobs = Jobs(Job(300, status: "in_progress"), Job(300, labels: "\"windows-latest\""));

        var result = Check(Runners(Runner("a")), jobs);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Queued);
    }

    [Fact]
    public void Jobs_from_several_runs_are_added_up()
    {
        var result = AbCiCapacity.Evaluate(Runners(Runner("a", busy: true)), [Jobs(Job(5)), Jobs(Job(6))], Now, 1, TimeSpan.FromSeconds(60), 1);

        Assert.Equal(2, result.Queued);
        Assert.False(result.Ok);
    }

    [Fact]
    public void The_command_reads_its_files_and_exits_1_when_the_pool_falls_behind()
    {
        var dir = Directory.CreateTempSubdirectory("ab-ci-capacity-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "runners.json"), Runners(Runner("a", busy: true)));
            Directory.CreateDirectory(Path.Combine(dir, "jobs"));
            File.WriteAllText(Path.Combine(dir, "jobs", "1.json"), Jobs(Job(5)));
            string[] args = ["ab-ci-capacity", "--runners", Path.Combine(dir, "runners.json"), "--jobs", Path.Combine(dir, "jobs"), "--now", "2026-11-23T12:00:00Z"];
            var output = new StringWriter();

            Assert.Equal(1, Program.Run(args, output));
            Assert.Equal(0, Program.Run([.. args, "--max-queued", "1"], new StringWriter()));
            Assert.Contains("1 ab-ci job(s) queued", output.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"runners\": 3}")]
    [InlineData("{}")]
    public void Input_this_check_cannot_read_throws_rather_than_passing(string runners)
        => Assert.ThrowsAny<Exception>(() => Check(runners, Jobs()));
}
