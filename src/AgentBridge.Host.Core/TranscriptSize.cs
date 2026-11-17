using AgentBridge.Protocol;

namespace AgentBridge.Host.Core;

internal sealed record TranscriptSize(long? Bytes, string? Unavailable, TranscriptSizeProvenance Provenance)
{
    public static TranscriptSize Read(string? path, string hostId, DateTimeOffset now)
    {
        var provenance = new TranscriptSizeProvenance(now, "file-size", hostId);
        TranscriptSize Missing(string reason) => new(null, reason, provenance);
        if (path is null) { return Missing("path-unavailable"); }
        try
        {
            return new(new FileInfo(path).Length, null, provenance);
        }
        catch (FileNotFoundException) { return Missing("file-not-found"); }
        catch (DirectoryNotFoundException) { return Missing("directory-not-found"); }
        catch (UnauthorizedAccessException) { return Missing("access-denied"); }
        catch (ArgumentException) { return Missing("invalid-path"); }
        catch (NotSupportedException) { return Missing("invalid-path"); }
        catch (IOException) { return Missing("io-error"); }
    }
}
