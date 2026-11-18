static class ReplyChannel
{
    internal static async Task<T> OpenAsync<T>(Func<Task> callerReady, Func<Task> targetReady,
        Func<Task<T>> open, Func<string> describe)
    {
        var stage = "peer presence";
        try
        {
            await Task.WhenAll(callerReady(), targetReady());
            stage = "channel handshake";
            return await open();
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Reply fixture timed out during {stage}: {describe()}", error);
        }
    }
}
