namespace AgentBridge.Host.Core;

internal static class TranscriptFileSize
{
    public static (long? Bytes, string? Unavailable) Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return (null, "path-unavailable"); }
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null)
            {
                if (file.ResolveLinkTarget(true) is not FileInfo target) { return (null, "not-a-file"); }
                file = target;
            }
            return (file.Length, null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (null, "not-found"); }
        catch (UnauthorizedAccessException) { return (null, "access-denied"); }
        catch (IOException) { return (null, "io-error"); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return (null, "invalid-path"); }
    }
}
