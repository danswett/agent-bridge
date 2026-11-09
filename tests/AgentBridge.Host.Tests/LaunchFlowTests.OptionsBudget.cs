using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1291 (#95 S1): launch.options answers within a whole-call budget even while a folder check is stuck,
// offering only the sessions whose folders were checked. Every wait below is on a data event
// (the stuck check being reached, the call returning, the log line); the budget is a fake clock's.
public sealed partial class LaunchFlowTests
{
    const string Newest = "11111111-1111-4111-8111-111111111111";
    const string Stuck = "22222222-2222-4222-8222-222222222222";
    const string Oldest = "33333333-3333-4333-8333-333333333333";

    sealed class Held
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string?> Checked = new();
        public readonly TaskCompletionSource<string> Logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // newest first: App (checked), API (the stuck one when hold), App (older).
    async Task<(LaunchService Service, FakeTimeProvider Time, Held Held)> BudgetedAsync(bool hold)
    {
        var now = DateTimeOffset.UtcNow;
        Resumable(Newest, app, "Newest", now.AddHours(-1));
        Resumable(Stuck, api, "Stuck", now.AddHours(-2));
        Resumable(Oldest, app, "Oldest", now.AddHours(-3));
        await source.RefreshAsync();
        var held = new Held();
        var time = new FakeTimeProvider(now);
        var service = new LaunchService(source, probe, screens, starter, locator, new AgencyCatalog(agency), new LaunchServiceOptions
        {
            Time = time,
            CopilotHome = f.CopilotHome,
            ClaudeHome = f.ClaudeHome,
            CodexHome = f.CodexHome,
            Config = Config(),
            PlatformSupported = true,
            IsSystemDirectory = _ => false,
            PassEnded = () => held.Ended.TrySetResult(),
            BeforeFolderCheck = (folder, _) =>
            {
                held.Checked.Enqueue(folder);
                if (hold && folder == api)
                {
                    held.Reached.TrySetResult();
                    // A stuck git cannot be cancelled mid-call, so this does not watch the token either.
                    held.Release.Wait();
                }
            },
        }, log: line => { if (line.Contains("left out")) { held.Logged.TrySetResult(line); } });
        return (service, time, held);
    }

    [Fact]
    public async Task A_stuck_folder_check_leaves_launch_options_returning_at_the_budget_with_the_checked_sessions_only()
    {
        var (service, time, held) = await BudgetedAsync(hold: true);
        try
        {
            var call = service.OptionsResolvedAsync();
            await held.Reached.Task.WaitAsync(Patience);
            Assert.False(call.IsCompleted);

            time.Advance(TimeSpan.FromSeconds(60));
            var resolved = await call.WaitAsync(Patience);

            Assert.Equal([Newest], resolved.Options.Resumable.Select(r => r.Id));
            Assert.Equal(new LaunchService.Omission(2, "deadline"), resolved.Omitted);
            Assert.Contains("left out 2 resumable sessions", await held.Logged.Task.WaitAsync(Patience));
        }
        finally { held.Release.Set(); }
    }

    [Fact]
    public async Task A_pass_the_caller_stopped_waiting_for_starts_no_further_folder_check()
    {
        var (service, time, held) = await BudgetedAsync(hold: true);
        var call = service.OptionsResolvedAsync();
        await held.Reached.Task.WaitAsync(Patience);
        time.Advance(TimeSpan.FromSeconds(60));
        await call.WaitAsync(Patience);

        held.Release.Set();
        // The worker, once let go, sees its token and stops: its last check is the stuck one.
        await held.Ended.Task.WaitAsync(Patience);
        Assert.Equal([app, api], held.Checked);
    }

    [Fact]
    public async Task A_fast_pass_is_unchanged_by_the_budget()
    {
        var (service, _, held) = await BudgetedAsync(hold: false);

        var resolved = await service.OptionsResolvedAsync();

        Assert.Equal([Newest, Stuck, Oldest], resolved.Options.Resumable.Select(r => r.Id));
        Assert.Null(resolved.Omitted);
        Assert.False(held.Logged.Task.IsCompleted);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_throws_and_is_not_a_partial_list()
    {
        var (service, _, held) = await BudgetedAsync(hold: true);
        using var cancel = new CancellationTokenSource();
        try
        {
            var call = service.OptionsResolvedAsync(cancel.Token);
            await held.Reached.Task.WaitAsync(Patience);

            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Patience));
        }
        finally { held.Release.Set(); }
    }
}
