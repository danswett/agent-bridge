namespace AgentBridge.Host.Delivery.Copilot;

internal delegate void JsonlLine(ReadOnlySpan<byte> line, long start);

// Reads a transcript a line at a time without parsing it: callers look for a few markers in
// the bytes and parse only the lines that carry them, so a transcript of hundreds of
// megabytes costs a scan, not a parse. Only reads (FileShare.ReadWrite | Delete: the CLI
// keeps writing).
internal static class Jsonl
{
    const int MaxLineBytes = 32 << 20;

    public static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    // Every complete line from `from`, with the offset it starts at; a line longer than any
    // event is skipped. A window starting inside a line (skipFirst) skips that partial line.
    // Returns the offset just after the last complete line, so a trailing line still being
    // written is read whole next time; null when the file cannot be opened.
    public static long? Lines(string path, long from, bool skipFirst, JsonlLine line)
    {
        using var stream = Open(path);
        if (stream is null) { return null; }
        stream.Seek(from, SeekOrigin.Begin);
        var skipping = skipFirst && from > 0;
        var buffer = new List<byte>(4096);
        var oversized = false;
        var chunk = new byte[64 * 1024];
        long position = from, start = from, scannedTo = from;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = chunk[i];
                position++;
                if (b != (byte)'\n')
                {
                    if (!skipping && !oversized)
                    {
                        if (buffer.Count >= MaxLineBytes) { oversized = true; buffer.Clear(); }
                        else { buffer.Add(b); }
                    }
                    continue;
                }
                if (skipping) { skipping = false; }
                else if (!oversized) { line(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), start); }
                buffer.Clear();
                oversized = false;
                scannedTo = position;
                start = position;
            }
        }
        return scannedTo;
    }

    static FileStream? Open(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
