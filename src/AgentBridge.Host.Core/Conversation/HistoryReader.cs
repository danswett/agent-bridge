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

// What the session knows that a page's own records may not say: the main agent's own delegates
// (by id, with their names) and the task calls that started them, for a report whose start is on
// an older page; and the turns whose prompt the host knew an agent sent through it (#489), which
// the transcript does not mark.
internal sealed record HistorySeed(IReadOnlyDictionary<string, string?> Delegates, IReadOnlyDictionary<string, string> Calls)
{
    public IReadOnlySet<string> AgentTurns { get; init; } = new HashSet<string>();

    // Who made the delegates' calls the session keeps (BackgroundWork.CallOwners): a permission
    // prompt or a nested start naming a call made on an older page is still that delegate's.
    public IReadOnlyDictionary<string, string> CallOwners { get; init; } = new Dictionary<string, string>();
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

    public HistoryResult Read(string sessionId, AgentKind agent, string? path, string? before, int limit, HistorySeed seed, bool remoteApproval)
    {
        if (HistoryDialect.For(agent) is not { } dialect) { return HistoryResult.NotAvailable(NoHistory); }
        if (path is null) { return HistoryResult.NotAvailable(NotFound); }
        SafeFileHandle handle;
        try { handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return HistoryResult.NotAvailable(NotFound); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HistoryResult.NotAvailable(NotRead); }
        using (handle)
        {
            try { return new Pass(this, handle, sessionId, dialect, new HistoryContext(seed.Delegates, seed.Calls, remoteApproval, seed.CallOwners), seed.AgentTurns).Page(before, limit); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HistoryResult.NotAvailable(NotRead); }
        }
    }

    // A record that may start a turn, found going back: where its line starts and ends (past its
    // newline), the turn's id, and what started it (null for one that could not be read).
    sealed record Start(long Offset, long End, byte[] Id, HistoryPrompt? Prompt, DateTimeOffset? At);

    sealed class Pass(HistoryReader reader, SafeFileHandle file, string sessionId, HistoryDialect dialect, HistoryContext context, IReadOnlySet<string> agentTurns)
    {
        delegate bool DroppedLine(ReadOnlySpan<byte> head, long offset);

        readonly HistoryBudgets b = reader._budgets;
        readonly long length = RandomAccess.GetLength(file);
        // What this request has read backwards (finding the last line's end, then prompts) against
        // ScanBytes, and parsed (prompts found, then turns) against PageBytes: one budget each for
        // the whole request, so no step of it reads past what the request may.
        long scanned;
        long parsed;
        // Lines Back has parsed looking for turns' starts.
        int examined;

        public HistoryResult Page(string? before, int limit)
        {
            var (fingerprint, firstAt) = First();
            long end;
            var depth = 0;
            if (before is null)
            {
                if (LastLineEnd() is not { } last) { return new HistoryResult { Turns = [], Start = new HistoryStart { Limited = true }, Partial = true }; }
                end = last;
            }
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

            // Newest first, as many as the page's parsing budget allows, at least one. Their
            // prompts were charged as Back parsed them.
            var turns = new List<HistoryTurn>();
            var built = new List<TurnBuilder>();
            for (var i = 0; i < starts.Count; i++)
            {
                var spanEnd = i == 0 ? end : starts[i - 1].Offset;
                var cost = Math.Min(spanEnd - starts[i].End, b.TurnBytes);
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
            // What the served turns say: one built and then left out of the page says nothing of it.
            var result = new HistoryResult { Turns = page, Partial = page.Any(t => t.Truncated == true || t.Unreadable == true) ? true : null };
            if (n == starts.Count && (reachedStart || oldest.Offset == FirstPrompt()))
            {
                return result with { Start = new HistoryStart { At = firstAt } };
            }
            if (served >= HistoryLimits.Kept) { return result with { Start = new HistoryStart { Limited = true } }; }
            var next = new HistoryCursor(oldest.Offset, served, oldest.Id, fingerprint, HistoryCursor.SessionOf(sessionId));
            return result with { Before = next.Seal(reader._key) };
        }

        // The transcript's fingerprint (its first record, or the first 4 KiB of one longer), and
        // the time of its first record that has one: the start of the session. What it parses is
        // the request's, as all its parsing is (found in review of #292).
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
                if (!Charge(line.Length)) { return false; }
                using var doc = Parse(line);
                if (doc is null) { return true; }
                dialect.First(doc.RootElement, context);
                at = Json.Time(doc.RootElement, "timestamp");
                return at is null && ++looked < 64;
            });
            return (fingerprint, at);
        }

        // Parsing this many bytes more is within the request's budget, and is charged to it.
        bool Charge(long bytes)
        {
            if (parsed + bytes > b.PageBytes) { return false; }
            parsed += bytes;
            return true;
        }

        long? _firstPrompt;
        bool _firstPromptLooked;

        // Where the first turn starts, when it is within the first bytes looked at and the budget:
        // null when it is not known, so a page is never taken for the start when it may not be. A
        // line too long to read whose start may begin a turn is one, as Examine takes it, so a turn
        // that can't be read is never skipped past (found in review of #292).
        long? FirstPrompt()
        {
            if (_firstPromptLooked) { return _firstPrompt; }
            _firstPromptLooked = true;
            Forward(0, Math.Min(length, b.FirstBytes), (line, offset) =>
            {
                if (!dialect.MayStart(line.Span)) { return true; }
                if (!Charge(line.Length)) { return false; }
                using var doc = Parse(line);
                if (doc is null || dialect.Start(doc.RootElement) is not null)
                {
                    _firstPrompt = offset;
                    return false;
                }
                return true;
            }, dropped: (head, offset) =>
            {
                if (!dialect.MayStart(head)) { return true; }
                _firstPrompt = offset;
                return false;
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

        // Just past the last newline: a line still being written is not read. Null when no newline
        // is within the request's backward budget: a line that long is not read anyway.
        long? LastLineEnd()
        {
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var pos = length;
                while (pos > 0)
                {
                    if (scanned >= b.ScanBytes) { return null; }
                    var n = (int)Math.Min(64 * 1024, pos);
                    pos -= n;
                    scanned += n;
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
        // Every line is searched whole for a turn's marks (TurnMarks) as its chunks go by, so a
        // line costs a parse only when it may start a turn, wherever in it its type is.
        (List<Start> Starts, bool ReachedStart) Back(long end, int want)
        {
            var starts = new List<Start>();
            var marks = dialect.Marks;
            var keep = marks.Longest - 1;
            // The first bytes of the line being read that a later chunk held, so a mark split
            // between two chunks is found.
            var carry = new byte[keep];
            var carried = 0;
            (bool Any, bool None) found = default;
            var next = end;
            var chunk = ArrayPool<byte>.Shared.Rent(b.ChunkBytes);
            try
            {
                var pos = end;
                while (pos > 0)
                {
                    if (scanned >= b.ScanBytes) { return (starts, false); }
                    var n = (int)Math.Min(b.ChunkBytes, pos);
                    pos -= n;
                    scanned += n;
                    ReadAt(chunk.AsSpan(0, n), pos);
                    var segmentEnd = n;
                    for (var i = n - 1; i >= 0; i--)
                    {
                        if (chunk[i] != '\n') { continue; }
                        Mark(chunk.AsSpan(i + 1, segmentEnd - i - 1), segmentEnd == n);
                        if (!Line(pos + i + 1, chunk.AsSpan(0, n), pos)) { return (starts, false); }
                        if (starts.Count >= want) { return (starts, next == 0); }
                        segmentEnd = i;
                    }
                    // The rest belongs to a line that began in an earlier chunk, or at the start.
                    var rest = chunk.AsSpan(0, segmentEnd);
                    Mark(rest, segmentEnd == n);
                    Carry(rest, segmentEnd == n);
                    if (pos == 0 && !Line(0, chunk.AsSpan(0, n), 0)) { return (starts, false); }
                }
                return (starts, next == 0);
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }

            void Mark(ReadOnlySpan<byte> piece, bool joined)
            {
                var f = marks.Find(piece);
                found = (found.Any || f.Any, found.None || f.None);
                if (!joined || carried == 0) { return; }
                var tail = piece[Math.Max(0, piece.Length - keep)..];
                Span<byte> window = stackalloc byte[tail.Length + carried];
                tail.CopyTo(window);
                carry.AsSpan(0, carried).CopyTo(window[tail.Length..]);
                var g = marks.Find(window);
                found = (found.Any || g.Any, found.None || g.None);
            }

            void Carry(ReadOnlySpan<byte> piece, bool joined)
            {
                Span<byte> merged = stackalloc byte[Math.Min(keep, piece.Length) + (joined ? carried : 0)];
                piece[..Math.Min(keep, piece.Length)].CopyTo(merged);
                if (joined) { carry.AsSpan(0, carried).CopyTo(merged[Math.Min(keep, piece.Length)..]); }
                carried = Math.Min(keep, merged.Length);
                merged[..carried].CopyTo(carry);
            }

            // The line [lineStart, next) is whole: examined if it may start a turn. False when the
            // request's parsing budget stops it.
            bool Line(long lineStart, ReadOnlySpan<byte> inChunk, long chunkAt)
            {
                var go = lineStart >= next || found is not (true, false) || Examine(lineStart, next, inChunk, chunkAt, starts);
                next = lineStart;
                found = default;
                carried = 0;
                return go;
            }
        }

        // One line found going back, [start, end) with its newline, which carries a turn's marks:
        // a turn's start, or not. False, adding nothing, when parsing it would spend more than the
        // request may (PageBytes; the first line examined is always read).
        bool Examine(long start, long end, ReadOnlySpan<byte> chunk, long chunkAt, List<Start> starts)
        {
            var content = ContentLength(start, end);
            if (content <= 0) { return true; }
            if (content > b.MaxLineBytes)
            {
                // Too long to read. The live parser can see only such a line's start
                // (TranscriptTail.Dropped), so only one whose start may begin a turn is taken as
                // a turn that can't be read, as there.
                var head = Head(start, content, chunk, chunkAt);
                if (!dialect.MayStart(head)) { return true; }
                starts.Add(new Start(start, end, TurnIds.RawOfLong(head, content, start), null, null));
                return true;
            }
            // Every line parsed is charged, a turn's start or not: a record carrying a turn's mark in
            // its text is parsed to learn it is not one, and charging only after the first prompt
            // let those cost without bound (found in review of #292). The first is always read, so a
            // page can always make progress past one line up to MaxLineBytes.
            if (examined++ > 0 && parsed + content > b.PageBytes) { return false; }
            parsed += content;
            var line = new byte[content];
            ReadAt(line, start);
            using var doc = Parse(line);
            if (doc is null)
            {
                starts.Add(new Start(start, end, TurnIds.Raw(line), null, null));
                return true;
            }
            if (dialect.Start(doc.RootElement) is { } prompt)
            {
                starts.Add(new Start(start, end, TurnIds.Raw(line), prompt, Json.Time(doc.RootElement, "timestamp")));
            }
            return true;
        }

        // A line's first HeadBytes, from the chunk in hand when it holds them.
        byte[] Head(long start, long content, ReadOnlySpan<byte> chunk, long chunkAt)
        {
            var headLength = (int)Math.Min(HeadBytes, content);
            var head = new byte[headLength];
            var inChunk = start - chunkAt;
            if (inChunk >= 0 && inChunk + headLength <= chunk.Length) { chunk.Slice((int)inChunk, headLength).CopyTo(head); }
            else { ReadAt(head, start); }
            return head;
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
                return TurnIds.RawOfLong(head, content, start);
            }
            var line = new byte[Math.Max(0, content)];
            ReadAt(line, start);
            return TurnIds.Raw(line);
        }

        TurnBuilder Builder(Start s)
        {
            var id = Attachments.Base64Url(s.Id);
            // A prompt the host knew an agent sent (#489) is the agent's here too, as it was live.
            var prompt = s.Prompt is { From: null } p && agentTurns.Contains(id) ? p with { From = UserMessageFrom.Agent } : s.Prompt;
            return new TurnBuilder(id, s.At ?? DateTimeOffset.UnixEpoch, prompt) { Unreadable = s.Prompt is null };
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
            }
            // A turn that could not be read holds only its id and time (v1-history.md). Its records
            // are still read into a turn nobody sees, so the delegates started in it are known by
            // name when they report back in a later one.
            var into = turn.Unreadable ? new TurnBuilder(turn.Id, turn.At, null) : turn;
            context.TurnBegins();
            var at = s.At;
            Forward(from, spanEnd, (line, _) =>
            {
                if (!dialect.Wants(line.Span)) { return true; }
                using var doc = Parse(line);
                if (doc is null)
                {
                    turn.Truncated = true;
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
        // MaxLineBytes is passed over, as the tail passes over it, and dropped told of it with its
        // first bytes. Unaligned, the fragment before the first newline belongs to a line that began
        // earlier, and is dropped.
        void Forward(long from, long to, Func<ReadOnlyMemory<byte>, long, bool> onLine, bool aligned = true, DroppedLine? dropped = null)
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
                        if (!dropping && oversized && dropped is not null)
                        {
                            var content = ContentLength(lineStart, pos + i + newline + 1);
                            var head = new byte[(int)Math.Min(HeadBytes, content)];
                            ReadAt(head, lineStart);
                            if (!dropped(head, lineStart)) { return; }
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
