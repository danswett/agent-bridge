using Stopwatch = System.Diagnostics.Stopwatch;

// Deletes a test's temp folder (#2558). Windows can still hold a just-exited program's image, or a scanner's handle,
// when a teardown deletes the folder, so the delete is retried until patience runs out. A folder that stays held is left
// behind and reported false: the test it belongs to has already passed or failed on its own account.
internal static class TempFolder
{
    public static bool TryDelete(string path, TimeSpan patience)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Delete(path, recursive: true); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (clock.Elapsed >= patience) { return false; }
                Thread.Sleep(100);
            }
        }
    }
}
