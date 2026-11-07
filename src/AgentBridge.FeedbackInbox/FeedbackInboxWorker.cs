using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentBridge.FeedbackInbox;

public sealed class FeedbackInboxWorker(
    FeedbackInboxOptions options,
    FeedbackInboxProcessor processor,
    ILogger<FeedbackInboxWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, time);
        while (true)
        {
            try
            {
                await processor.ProcessAsync(stoppingToken);
            }
            catch (FeedbackInboxRepositoryNotPrivateException)
            {
                logger.LogError(
                    "Feedback inbox is paused because its configured repository is public; pending bundles are untouched. Rechecking after {PollInterval}.",
                    options.PollInterval);
            }
            catch (FeedbackInboxTransientException)
            {
                logger.LogWarning(
                    "Feedback inbox sweep failed transiently; pending bundles will be retried after {PollInterval}.",
                    options.PollInterval);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) { break; }
        }
    }
}
