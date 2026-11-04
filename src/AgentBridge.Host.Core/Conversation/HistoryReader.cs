using System.Buffers;
using System.Text.Json;
using AgentBridge.Protocol;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Core.Conversation;

// How hard a history request may look (docs/protocol/v1-history.md, Caps): bytes read backwards
// looking for prompts, bytes parsed of any one turn and of a page, and the longest line read at all.
internal sealed record HistoryBudgets
{
    public long ScanBytes { get; init; } = 256L * 1024 * 1024;
    public long TurnBytes { get; init; } = 8L * 1024 * 1024;
    public long PageBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxLineBytes { get; init; } = 32 * 1024 * 1024;
    // Where the session's first record and its first prompt are looked for.
    public long FirstBytes { get; init; } = 64L * 1024 * 1024;
    public int ChunkBytes { get; init; } = 1024 * 1024;
}

// One request's read of a session's transcript (docs/protocol/v1-history.md): backwards from the
// cursor (or the end) to the prompts that start the page's turns, by byte search, parsing only the
// lines that search picks; then forwards through each turn, parsing only the lines its dialect
// wants. Read-only and shared, as the tail reads: the agent keeps writing meanwhile, and only
// whole lines before where this read began are read.
internal sealed class HistoryReader(HistoryBudgets budgets, byte[] key)
{
    readonly HistoryBudgets _budgets = budgets;
    readonly byte[] _key = key;
    const int HeadBytes = 4096;

    public const string NotFound = "this session's transcript can't be found";
    public const string NotRead = "this session's transcript can't be read";
    public const string NoHistory = "this machine keeps no history for this agent";

    // delegates: the main agent's own delegates the session lists now, by id, with their names, for
    // a report whose start is on an older page.
    public HistoryResult Read(string sessionId, AgentKind agent, string? path, string? before, int limit,
        IReadOnlyDictionary<string, string?> delegates, bool remoteApproval)
    {
        if (HistoryDialect.For(agent) is not { } dialect) { return HistoryResult.NotAvailable(NoHistory); }
        if (path is null) { return HistoryResult.NotAvailable(NotFound); }
        SafeFileHandle handle;
        try { handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return HistoryResult.NotAvailable(NotFound); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HistoryResult.NotAvailable(NotRead); }
        using (handle)
        {
            try { return new Pass(this, handle, sessionId, dialect, new HistoryContext(delegates, remoteApproval)).Page(before, limit); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HistoryResult.NotAvailable(NotRead); }
        }
    }

    // A record that may start a turn, found going back: where its line starts and ends (past its
    // newline), the turn's id, and what started it (null for one that could not be read).
    sealed record Start(long Offset, long End, byte[] Id, HistoryPrompt? Prompt, DateTimeOffset? At);

    sealed class Pass(HistoryReader reader, SafeFileHandle file, string sessionId, HistoryDialect dialect, HistoryContext context)
    {
        readonly HistoryBudgets b = reader._budgets;
        readonly long length = RandomAccess.GetLength(file);
        bool partial;

        public HistoryResult Page(string? before, int limit)
        {
            var (fingerprint, firstAt) = First();
            long end;
            var depth = 0;
            if (before is null) { end = LastLineEnd(); }
            else
            {
                if (HistoryCursor.Open(before, reader._key) is not { } cursor || !Fits(cursor, fingerprint)) { return HistoryResult.StaleCursor; }
                end = cursor.Offset;
                depth = cursor.Depth;
            }
            var want = Math.Min(limit, HistoryLimits.Kept - depth);
            if (want <= 0) { return HistoryResult.StaleCursor; }

            var (starts, reachedStart) = Back(end, want);
            if (starts.Count == 0)
            {
                // Nothing before it: the start of the session, or as far back as this host looks.
                return reachedStart
                    ? new HistoryResult { Turns = [], Start = new HistoryStart { At = firstAt } }
                    : new HistoryResult { Turns = [], Start = new HistoryStart { Limited = true }, Partial = true };
            }

            // Newest first, as many as the page's parsing budget allows, at least one.
            var turns = new List<HistoryTurn>();
            long parsed = 0;
            var built = new List<TurnBuilder>();
            for (var i = 0; i < starts.Count; i++)
            {
                var spanEnd = i == 0 ? end : starts[i - 1].Offset;
                var cost = Math.Min(spanEnd - starts[i].Offset, b.TurnBytes + (starts[i].End - starts[i].Offset));
                if (i > 0 && parsed + cost > b.PageBytes) { break; }
                parsed += cost;
                built.Add(Builder(starts[i]));
            }
            // Built oldest first, so a delegate's start is known before its report.
            for (var i = built.Count - 1; i >= 0; i--)
            {
                var spanEnd = i == 0 ? end : starts[i - 1].Offset;
                Fill(built[i], starts[i], spanEnd);
            }
            for (var i = 0; i < built.Count; i++) { turns.Add(built[i].Build(latest: before is null && i == 0)); }

            var n = HistoryBudget.FitsInPage(turns);
            var page = turns.Take(n).Reverse().ToList();
            var oldest = starts[n - 1];
            var served = depth + n;
            var result = new HistoryResult { Turns = page, Partial = partial ? true : null };
            if (n == starts.Count && (reachedStart || oldest.Offset == FirstPrompt()))
            {
                return result with { Start = new HistoryStart { At = firstAt } };
            }
            if (served >= HistoryLimits.Kept) { return result with { Start = new HistoryStart { Limited = true } }; }
            var next = new HistoryCursor(oldest.Offset, served, oldest.Id, fingerprint, HistoryCursor.SessionOf(sessionId));
            return result with { Before = next.Seal(reader._key) };
        }

        // The transcript's fingerprint (its first record, or the first 4 KiB of one longer), and
        // the time of its first record that has one: the start of the session.
        (byte[] Fingerprint, DateTimeOffset? At) First()
        {
            var head = new byte[(int)Math.Min(HeadBytes, length)];
            ReadAt(head, 0);
            var newline = head.AsSpan().IndexOf((byte)'\n');
            var fingerprint = HistoryCursor.FingerprintOf(newline >= 0 ? head.AsSpan(0, newline + 1) : head);
            DateTimeOffset? at = null;
            var looked = 0;
            Forward(0, Math.Min(length, b.FirstBytes), (line, _) =>
            {
                using var doc = Parse(line);
                if (doc is null) { return true; }
                dialect.First(doc.RootElement, context);
                at = Json.Time(doc.RootElement, "timestamp");
                return at is null && ++looked < 64;
            });
            return (fingerprint, at);
        }

        long? _firstPrompt;
        bool _firstPromptLooked;

        // Where the first turn starts, when it is within the first bytes looked at.
        long? FirstPrompt()
        {
            if (_firstPromptLooked) { return _firstPrompt; }
            _firstPromptLooked = true;
            Forward(0, Math.Min(length, b.FirstBytes), (line, offset) =>
            {
                if (!dialect.MayStart(Head(line.Span))) { return true; }
                using var doc = Parse(line);
                if (doc is null || dialect.Start(doc.RootElement) is not null)
                {
                    _firstPrompt = offset;
                    return false;
                }
                return true;
            });
            return _firstPrompt;
        }

        // A cursor this host sealed still names a turn of this transcript: the same transcript, and
        // the record it names still starts a line where it says.
        bool Fits(HistoryCursor cursor, byte[] fingerprint)
        {
            if (!cursor.Session.AsSpan().SequenceEqual(HistoryCursor.SessionOf(sessionId)) || !cursor.Fingerprint.AsSpan().SequenceEqual(fingerprint)) { return false; }
            if (cursor.Offset >= length || cursor.Depth >= HistoryLimits.Kept) { return false; }
            if (cursor.Offset > 0)
            {
                var previous = new byte[1];
                ReadAt(previous, cursor.Offset - 1);
                if (previous[0] != '\n') { return false; }
            }
            var lineEnd = NextNewline(cursor.Offset);
            if (lineEnd is null) { return false; }
            var id = IdOf(cursor.Offset, lineEnd.Value, out _);
            return id.AsSpan().SequenceEqual(cursor.TurnId);
        }

        // Just past the last newline: a line still being written is not read.
        long LastLineEnd()
        {
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var pos = length;
                while (pos > 0)
                {
                    var n = (int)Math.Min(64 * 1024, pos);
                    pos -= n;
                    ReadAt(chunk.AsSpan(0, n), pos);
                    var i = chunk.AsSpan(0, n).LastIndexOf((byte)'\n');
                    if (i >= 0) { return pos + i + 1; }
                }
                return 0;
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }

        long? NextNewline(long from)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var pos = from;
                while (pos < length && pos - from <= b.ScanBytes)
                {
                    var n = (int)Math.Min(64 * 1024, length - pos);
                    ReadAt(chunk.AsSpan(0, n), pos);
                    var i = chunk.AsSpan(0, n).IndexOf((byte)'\n');
                    if (i >= 0) { return pos + i + 1; }
                    pos += n;
                }
                return null;
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }

        // Going back from end, the records that start the newest `want` turns before it, newest
        // first; and whether it got back to the transcript's start, so nothing is before them.
        (List<Start> Starts, bool ReachedStart) Back(long end, int want)
        {
            var starts = new List<Start>();
            var chunk = ArrayPool<byte>.Shared.Rent(b.ChunkBytes);
            try
            {
                var pos = end;
                var next = end;
                long scanned = 0;
                while (pos > 0 && starts.Count < want)
                {
                    if (scanned >= b.ScanBytes) { return (starts, false); }
                    var n = (int)Math.Min(b.ChunkBytes, pos);
                    pos -= n;
                    scanned += n;
                    ReadAt(chunk.AsSpan(0, n), pos);
                    for (var i = n - 1; i >= 0 && starts.Count < want; i--)
                    {
                        if (chunk[i] != '\n') { continue; }
                        var lineStart = pos + i + 1;
                        if (lineStart < next) { Examine(lineStart, next, chunk.AsSpan(0, n), pos, starts); }
                        next = lineStart;
                    }
                    if (pos == 0 && next > 0 && starts.Count < want) { Examine(0, next, chunk.AsSpan(0, n), 0, starts); next = 0; }
                }
                return (starts, pos == 0 && next == 0);
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }

        // One line found going back, [start, end) with its newline: a turn's start, or not.
        void Examine(long start, long end, ReadOnlySpan<byte> chunk, long chunkAt, List<Start> starts)
        {
            // Most lines are not looked at past their start, so their length is only roughly known
            // (a carriage return before the newline or not) until one may start a turn.
            var rough = end - start - 1;
            if (rough <= 0) { return; }
            var headLength = (int)Math.Min(HeadBytes, rough);
            ReadOnlySpan<byte> head;
            var inChunk = start - chunkAt;
            if (inChunk + headLength <= chunk.Length) { head = chunk.Slice((int)inChunk, headLength); }
            else
            {
                var h = new byte[headLength];
                ReadAt(h, start);
                head = h;
            }
            if (!dialect.MayStart(head)) { return; }
            var content = ContentLength(start, end);
            if (content <= 0) { return; }
            if (head.Length > content) { head = head[..(int)content]; }
            if (content > b.MaxLineBytes)
            {
                // Too long to read: if it starts a turn, that turn can't be read.
                starts.Add(new Start(start, end, TurnIds.RawOfLong(head, content), null, null));
                partial = true;
                return;
            }
            var line = new byte[content];
            ReadAt(line, start);
            using var doc = Parse(line);
            if (doc is null)
            {
                starts.Add(new Start(start, end, TurnIds.Raw(line), null, null));
                partial = true;
                return;
            }
            if (dialect.Start(doc.RootElement) is { } prompt)
            {
                starts.Add(new Start(start, end, TurnIds.Raw(line), prompt, Json.Time(doc.RootElement, "timestamp")));
            }
        }

        // A line's length without its newline and any carriage return before it, as the tail reads it.
        long ContentLength(long start, long end)
        {
            var content = end - start - 1;
            if (content > 0)
            {
                var last = new byte[1];
                ReadAt(last, end - 2);
                if (last[0] == '\r') { content--; }
            }
            return content;
        }

        byte[] IdOf(long start, long end, out long content)
        {
            content = ContentLength(start, end);
            var headLength = (int)Math.Min(HeadBytes, Math.Max(0, content));
            if (content > b.MaxLineBytes)
            {
                var head = new byte[headLength];
                ReadAt(head, start);
                return TurnIds.RawOfLong(head, content);
            }
            var line = new byte[Math.Max(0, content)];
            ReadAt(line, start);
            return TurnIds.Raw(line);
        }

        TurnBuilder Builder(Start s)
        {
            var id = Attachments.Base64Url(s.Id);
            return new TurnBuilder(id, s.At ?? DateTimeOffset.UnixEpoch, s.Prompt) { Unreadable = s.Prompt is null };
        }

        // The rest of a turn after its prompt: everything up to the next turn, or its last TurnBytes
        // when it is longer, which leaves the turn marked as read only in part.
        void Fill(TurnBuilder turn, Start s, long spanEnd)
        {
            var from = s.End;
            if (spanEnd - from > b.TurnBytes)
            {
                from = spanEnd - b.TurnBytes;
                turn.Truncated = true;
                partial = true;
            }
            // A turn that could not be read holds only its id and time (v1-history.md). Its records
            // are still read into a turn nobody sees, so the delegates started in it are known by
            // name when they report back in a later one.
            var into = turn.Unreadable ? new TurnBuilder(turn.Id, turn.At, null) : turn;
            var at = s.At;
            Forward(from, spanEnd, (line, _) =>
            {
                if (!dialect.Wants(line.Span)) { return true; }
                using var doc = Parse(line);
                if (doc is null)
                {
                    turn.Truncated = true;
                    partial = true;
                    return true;
                }
                at = Json.Time(doc.RootElement, "timestamp") ?? at;
                // A turn whose own start could not be read is placed by the first time after it.
                if (s.At is null && at is { } first && turn.At == DateTimeOffset.UnixEpoch) { turn.At = first; }
                into.Record(Attachments.Base64Url(TurnIds.Raw(line.Span)));
                dialect.Apply(doc.RootElement, at ?? DateTimeOffset.UnixEpoch, into, context);
                return true;
            }, aligned: from == s.End);
        }

        // Calls onLine for each whole line in [from, to) until it says stop; a line longer than
        // MaxLineBytes is passed over, as the tail passes over it. Unaligned, the fragment before
        // the first newline belongs to a line that began earlier, and is dropped.
        void Forward(long from, long to, Func<ReadOnlyMemory<byte>, long, bool> onLine, bool aligned = true)
        {
            if (from >= to) { return; }
            var chunk = ArrayPool<byte>.Shared.Rent(b.ChunkBytes);
            var line = new ArrayBufferWriter<byte>();
            var oversized = false;
            var dropping = !aligned;
            var lineStart = from;
            try
            {
                var pos = from;
                while (pos < to)
                {
                    var n = (int)Math.Min(b.ChunkBytes, to - pos);
                    ReadAt(chunk.AsSpan(0, n), pos);
                    var i = 0;
                    while (i < n)
                    {
                        var newline = chunk.AsSpan(i, n - i).IndexOf((byte)'\n');
                        if (newline < 0)
                        {
                            if (!dropping && !oversized)
                            {
                                if (line.WrittenCount + (n - i) > b.MaxLineBytes) { oversized = true; line.ResetWrittenCount(); }
                                else { line.Write(chunk.AsSpan(i, n - i)); }
                            }
                            break;
                        }
                        if (!dropping && !oversized)
                        {
                            if (line.WrittenCount + newline > b.MaxLineBytes) { oversized = true; }
                            else
                            {
                                line.Write(chunk.AsSpan(i, newline));
                                var whole = line.WrittenMemory;
                                if (whole.Length > 0 && whole.Span[^1] == '\r') { whole = whole[..^1]; }
                                if (whole.Length > 0 && !onLine(whole, lineStart)) { return; }
                            }
                        }
                        dropping = false;
                        oversized = false;
                        line.ResetWrittenCount();
                        lineStart = pos + i + newline + 1;
                        i += newline + 1;
                    }
                    pos += n;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }

        static ReadOnlySpan<byte> Head(ReadOnlySpan<byte> line) => line.Length > HeadBytes ? line[..HeadBytes] : line;

        void ReadAt(Span<byte> buffer, long offset)
        {
            var done = 0;
            while (done < buffer.Length)
            {
                var n = RandomAccess.Read(file, buffer[done..], offset + done);
                if (n == 0) { throw new IOException("the transcript ended while it was read"); }
                done += n;
            }
        }

        static JsonDocument? Parse(ReadOnlyMemory<byte> line)
        {
            try
            {
                var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 });
                if (doc.RootElement.ValueKind == JsonValueKind.Object) { return doc; }
                doc.Dispose();
                return null;
            }
            catch (JsonException) { return null; }
        }
    }
}
