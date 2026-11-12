static class ReplyChannel
{
    internal static async Task<T> OpenAsync<T>(Func<Task> callerReady, Func<Task> targetReady,
        Func<Task<T>> open, Func<string> describe)
    {
        await callerReady();
        return await open();
    }
}
