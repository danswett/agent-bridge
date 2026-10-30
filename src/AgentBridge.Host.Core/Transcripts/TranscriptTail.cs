using System.Buffers;

namespace AgentBridge.Host.Core.Transcripts;

// Receives a transcript's lines: Skim for bytes outside the bounded window (cheap
// counting only), Parse for the window itself. Reset when the file was replaced.
internal interface ILineSink
{
    void Reset();
    void Skim(ReadOnlyMemory<byte> line);
    void Parse(ReadOnlyMemory<byte> line);
    // Bytes between the skimmed part and the window were never read at all.
    void Skipped() { }
}

internal enum TailOutcome { Missing, Unreadable, Unchanged, Appended, Restarted }

// Incremental, read-only tailing of one JSON Lines transcript.
//
// It remembers the offset just past the last complete line and reads only what was
// appended since. A partial last line is left for the next read: half-written answers
// used to be parsed, dropped, and never retried. A file that shrank, or whose bytes just
// before the offset changed (replaced, or shrank and regrew to a longer size), is read
// again from the start, because length alone cannot tell a same-size replacement apart.
//
// The first read (and the first after a restart) parses only the last
// InitialTailBytes; everything before is skimmed, so a 500 MB transcript costs a cheap
// byte scan once rather than a JSON parse of every line.
internal sealed class TranscriptTail(string path, SessionSourceOptions options)
{
    const int AnchorBytes = 64;
    const int ChunkBytes = 1024 * 1024;

    long _offset;
    // False when the offset is inside a line (a skim that jumped to the window and found
    // no newline yet), so the next read drops that fragment instead of parsing it.
    bool _aligned = true;
    byte[] _anchor = [];
    bool _started;

    public string Path { get; } = path;

    public long Offset => _offset;

    public TailOutcome Read(ILineSink sink)
    {
        FileStream stream;
        try
        {
            // Shared read, write and delete: the agent keeps the transcript open while it
            // writes, and nothing here may stop it renaming or deleting the file either.
            stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return TailOutcome.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return TailOutcome.Unreadable; }

        using (stream)
        {
            try { return ReadFrom(stream, sink); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return TailOutcome.Unreadable; }
        }
    }

    TailOutcome ReadFrom(FileStream stream, ILineSink sink)
    {
        var length = stream.Length;
        var restart = !_started;
        if (_started)
        {
            if (length < _offset || !AnchorStillMatches(stream)) { restart = true; }
            else if (length == _offset) { return TailOutcome.Unchanged; }
        }
        if (restart)
        {
            sink.Reset();
            _offset = 0;
            _aligned = true;
            _anchor = [];
            _started = true;
        }

        var from = _offset;
        // Only the first read (or one after a restart) is bounded. An append, however
        // big, is parsed whole: the parser still holds state from the last read, and a
        // skimmed tool completion or answer would leave it running or pending for good.
        var parseFrom = restart ? Math.Max(from, length - options.InitialTailBytes) : from;
        var position = from;
        var aligned = _aligned;
        if (parseFrom > from)
        {
            var skimEnd = Math.Min(parseFrom, from + options.MaxSkimBytes);
            // Lines that start before the window are skimmed whole, so the line straddling
            // its edge is neither lost nor seen twice.
            (position, aligned) = Scan(stream, from, length, aligned, stopAtLineStart: skimEnd, sink.Skim);
            if (position < skimEnd || (position < parseFrom && skimEnd < parseFrom))
            {
                // Skim budget spent (or one line longer than all of it): jump to the
                // window and drop the fragment of a line it starts in.
                position = parseFrom;
                aligned = IsLineStart(stream, position);
                sink.Skipped();
            }
        }
        // A trailing line with no newline yet stays unread: the offset stops just past the
        // last newline, so it is read whole once it is finished.
        (_offset, _aligned) = Scan(stream, position, length, aligned, stopAtLineStart: long.MaxValue, sink.Parse);
        _anchor = ReadAnchor(stream, _offset);
        return restart ? TailOutcome.Restarted : TailOutcome.Appended;
    }

    bool AnchorStillMatches(FileStream stream)
    {
        if (_anchor.Length == 0) { return true; }
        var now = ReadAnchor(stream, _offset);
        return now.AsSpan().SequenceEqual(_anchor);
    }

    static byte[] ReadAnchor(FileStream stream, long offset)
    {
        var count = (int)Math.Min(AnchorBytes, offset);
        if (count == 0) { return []; }
        var buffer = new byte[count];
        stream.Seek(offset - count, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return buffer;
    }

    static bool IsLineStart(FileStream stream, long position)
    {
        if (position == 0) { return true; }
        stream.Seek(position - 1, SeekOrigin.Begin);
        return stream.ReadByte() == '\n';
    }

    // Calls onLine for every complete line in [start, end) and returns the position just
    // past the last newline consumed. Unaligned, everything up to the first newline is a
    // fragment of a line that began earlier and is dropped. Stops once a line starts at or
    // beyond stopAtLineStart. Lines over MaxLineBytes are dropped, not buffered.
    (long Consumed, bool Aligned) Scan(FileStream stream, long start, long end, bool aligned, long stopAtLineStart, Action<ReadOnlyMemory<byte>> onLine)
    {
        if (start >= end) { return (start, aligned); }
        stream.Seek(start, SeekOrigin.Begin);
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var line = new ArrayBufferWriter<byte>();
        var oversized = false;
        var dropping = !aligned;
        var consumed = start;
        var position = start;
        try
        {
            while (position < end)
            {
                var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, end - position));
                if (read == 0) { break; }
                var i = 0;
                while (i < read)
                {
                    var newline = chunk.AsSpan(i, read - i).IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        Append(line, chunk.AsSpan(i, read - i), ref oversized, dropping);
                        break;
                    }
                    if (!dropping && !oversized)
                    {
                        ReadOnlyMemory<byte> whole;
                        if (line.WrittenCount == 0) { whole = chunk.AsMemory(i, newline); }
                        else
                        {
                            Append(line, chunk.AsSpan(i, newline), ref oversized, dropping);
                            whole = oversized ? ReadOnlyMemory<byte>.Empty : line.WrittenMemory;
                        }
                        if (!oversized)
                        {
                            if (whole.Length > 0 && whole.Span[^1] == '\r') { whole = whole[..^1]; }
                            if (whole.Length > options.MaxLineBytes) { whole = ReadOnlyMemory<byte>.Empty; }
                            if (!whole.IsEmpty) { onLine(whole); }
                        }
                    }
                    dropping = false;
                    oversized = false;
                    line.ResetWrittenCount();
                    consumed = position + i + newline + 1;
                    i += newline + 1;
                    if (consumed >= stopAtLineStart) { return (consumed, true); }
                }
                position += read;
            }
            // Past the last newline: a line start, unless the fragment being dropped has
            // not ended yet.
            return (consumed, !dropping);
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    void Append(ArrayBufferWriter<byte> line, ReadOnlySpan<byte> bytes, ref bool oversized, bool dropping)
    {
        if (dropping || oversized) { return; }
        if (line.WrittenCount + bytes.Length > options.MaxLineBytes) { oversized = true; line.ResetWrittenCount(); return; }
        line.Write(bytes);
    }
}
