using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Diagnostics;

// What a reader of the journal is given (#737): the newest records first, within both of the
// reader's limits, and how many were left out and why, so an empty or short journal is never
// mistaken for a quiet one.
public sealed record JournalSnapshot(
    IReadOnlyList<JournalEntry> Entries,
    // Kept, but beyond the reader's count or bytes.
    int OmittedByLimit,
    long EvictedByCount,
    long EvictedByAge,
    long EvictedByBytes,
    long RejectedOversized,
    long RejectedMalformed,
    // Lines of the journal's file that could not be read back, plus one for a file too long to
    // read whole.
    long DroppedOnLoad,
    // The file could not be read: this journal holds only what happened since it started, and
    // keeps it in memory only.
    bool LoadFailed,
    long PersistFailures);

public interface IDiagnosticsJournalReader
{
    JournalSnapshot Read(int maxEntries, int maxBytes);

    // The same read, for a reader that may have to give up (diagnostics.get's deadline, its
    // caller gone): it waits for the journal only while the token allows, so a writer slowed by
    // its file never holds a remote read, or the slot it took, past that.
    ValueTask<JournalSnapshot> ReadAsync(int maxEntries, int maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(maxEntries, maxBytes));
    }
}

public interface IDiagnosticsJournal : IDiagnosticsJournalReader
{
    // False when the record was refused (and counted): a kind or outcome not the journal's, or
    // a record that would not fit even without its frames.
    bool Record(JournalEntry draft);
}

// The local diagnostics journal: a ring of the last JournalContract.MaxEntries records, none
// older than MaxAge, together at most MaxTotalBytes as serialized. Every record is fitted to the
// contract (JournalContract.Fit) and serialized before it is kept, so what is kept, read and
// written to the file is the same closed form. Optionally kept in a file of those lines, appended
// to and rewritten once it would hold about twice the bound, so a restart keeps the history; the
// file is read back as strictly as anything else, and a line that is not a fit record is dropped
// and counted. Nothing here sends anything anywhere: reading is for the person's own feedback
// report and remote diagnostics, each of which asks for it.
public sealed class DiagnosticsJournal : IDiagnosticsJournal
{
    // The file is compacted to the ring before it would grow past this; a longer one (not
    // written by this) is read from its end only.
    internal const int MaxFileBytes = 2 * JournalContract.MaxTotalBytes + JournalContract.MaxEntryBytes;

    // The product's names are listed before any record is fitted or any line read back.
    static DiagnosticsJournal() => ExceptionShape.TrustProduct();

    readonly TimeProvider _time;
    readonly string? _file;
    // A semaphore rather than a lock: a record appends to the file while it holds it, and
    // ReadAsync waits for it cancellably.
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly LinkedList<(JournalEntry Entry, byte[] Bytes)> _items = new();
    long _bytes;
    long _next = 1;
    long _fileBytes;
    bool _persist;
    long _evictedByCount, _evictedByAge, _evictedByBytes, _rejectedOversized, _rejectedMalformed, _droppedOnLoad, _persistFailures;
    bool _loadFailed;

    public DiagnosticsJournal(TimeProvider? time = null, string? file = null)
    {
        _time = time ?? TimeProvider.System;
        _file = file;
        _persist = file is not null;
        if (file is not null) { Load(file); }
    }

    public bool Record(JournalEntry draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _gate.Wait();
        try
        {
            var now = Millis(_time.GetUtcNow());
            EvictAged(now);
            var fit = JournalContract.Fit(draft with { Seq = _next, AtUtc = now });
            if (fit is null) { _rejectedMalformed++; return false; }
            var (entry, bytes) = Encode(fit);
            if (bytes is null) { _rejectedOversized++; return false; }
            _next++;
            Add(entry, bytes);
            Append(bytes);
            return true;
        }
        finally { _gate.Release(); }
    }

    public JournalSnapshot Read(int maxEntries, int maxBytes)
    {
        Check(maxEntries, maxBytes);
        _gate.Wait();
        try { return Take(maxEntries, maxBytes); }
        finally { _gate.Release(); }
    }

    public async ValueTask<JournalSnapshot> ReadAsync(int maxEntries, int maxBytes, CancellationToken cancellationToken)
    {
        Check(maxEntries, maxBytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Take(maxEntries, maxBytes); }
        finally { _gate.Release(); }
    }

    static void Check(int maxEntries, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
    }

    // In memory and at most MaxEntries records, so short once the gate is held.
    JournalSnapshot Take(int maxEntries, int maxBytes)
    {
        maxEntries = Math.Min(maxEntries, JournalContract.MaxEntries);
        maxBytes = Math.Min(maxBytes, JournalContract.MaxTotalBytes);
        EvictAged(Millis(_time.GetUtcNow()));
        var entries = new List<JournalEntry>();
        long bytes = 0;
        for (var node = _items.Last; node is not null; node = node.Previous)
        {
            if (entries.Count == maxEntries || bytes + node.Value.Bytes.Length > maxBytes) { break; }
            entries.Add(node.Value.Entry);
            bytes += node.Value.Bytes.Length;
        }
        return new JournalSnapshot(entries, _items.Count - entries.Count, _evictedByCount, _evictedByAge, _evictedByBytes,
            _rejectedOversized, _rejectedMalformed, _droppedOnLoad, _loadFailed, _persistFailures);
    }

    static DateTimeOffset Millis(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    // The record serialized, and if it is too long, without its last frames (marked) until it
    // fits; null bytes when even that does not.
    static (JournalEntry Entry, byte[]? Bytes) Encode(JournalEntry entry)
    {
        while (true)
        {
            var bytes = JournalCodec.Write(entry);
            if (bytes.Length <= JournalContract.MaxEntryBytes) { return (entry, bytes); }
            if (entry.Exception is not { Frames.Count: > 0 } x) { return (entry, null); }
            entry = entry with
            {
                Exception = x with { Frames = x.Frames.Take(x.Frames.Count - 1).ToList() },
                Omitted = entry.Omitted | JournalOmissions.FramesTruncated,
            };
        }
    }

    void Add(JournalEntry entry, byte[] bytes)
    {
        _items.AddLast((entry, bytes));
        _bytes += bytes.Length;
        while (_items.Count > JournalContract.MaxEntries) { RemoveFirst(); _evictedByCount++; }
        while (_bytes > JournalContract.MaxTotalBytes) { RemoveFirst(); _evictedByBytes++; }
    }

    void EvictAged(DateTimeOffset now)
    {
        while (_items.First is { } first && now - first.Value.Entry.AtUtc > JournalContract.MaxAge) { RemoveFirst(); _evictedByAge++; }
    }

    void RemoveFirst()
    {
        _bytes -= _items.First!.Value.Bytes.Length;
        _items.RemoveFirst();
    }

    void Load(string file)
    {
        byte[] data = [];
        try
        {
            if (File.Exists(file))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var start = Math.Max(0, stream.Length - MaxFileBytes);
                stream.Seek(start, SeekOrigin.Begin);
                data = new byte[stream.Length - start];
                stream.ReadExactly(data);
                if (start > 0)
                {
                    // What came before is not read: counted once, and the cut line skipped.
                    _droppedOnLoad++;
                    var cut = Array.IndexOf(data, (byte)'\n');
                    data = cut < 0 ? Array.Empty<byte>() : data[(cut + 1)..];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A journal that cannot be read is not overwritten with a shorter one: this one is
            // kept in memory only, and says so.
            _loadFailed = true;
            _persist = false;
            return;
        }
        var now = Millis(_time.GetUtcNow());
        ReadOnlySpan<byte> rest = data;
        long last = 0;
        while (!rest.IsEmpty)
        {
            var end = rest.IndexOf((byte)'\n');
            var line = end < 0 ? rest : rest[..end];
            rest = end < 0 ? ReadOnlySpan<byte>.Empty : rest[(end + 1)..];
            if (!line.IsEmpty && line[^1] == (byte)'\r') { line = line[..^1]; }
            if (line.IsEmpty) { continue; }
            // Read back only as it was written: a fit record, in sequence, not from the future.
            if (!JournalCodec.TryRead(line, out var entry) || entry.Seq <= last || entry.AtUtc > now) { _droppedOnLoad++; continue; }
            last = entry.Seq;
            // Kept as the codec writes it now, not as the file had it.
            Add(entry, JournalCodec.Write(entry));
        }
        _next = last + 1;
        EvictAged(now);
        Compact();
    }

    void Append(byte[] bytes)
    {
        if (!_persist || _file is null) { return; }
        if (_fileBytes + bytes.Length + 1 > MaxFileBytes) { Compact(); return; }
        try
        {
            using var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            _fileBytes += bytes.Length + 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _persistFailures++; }
    }

    // The file rewritten as the ring holds now, through a temporary file moved over it.
    void Compact()
    {
        if (!_persist || _file is null) { return; }
        var temporary = _file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            long written = 0;
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var (_, bytes) in _items)
                {
                    stream.Write(bytes);
                    stream.WriteByte((byte)'\n');
                    written += bytes.Length + 1;
                }
            }
            File.Move(temporary, _file, overwrite: true);
            _fileBytes = written;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _persistFailures++; }
    }
}
