using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Core.Transcripts;
using BenchmarkDotNet.Attributes;

namespace AgentBridge.Benchmarks;

// The first read of a large transcript, as when the host starts or finds a session: the last
// 4 MiB parsed, everything before skimmed (TranscriptTail). About 21 MB, an ordinary Copilot
// events.jsonl (SessionSourceOptions.InitialTailBytes); the host reads every live one at start.
public class TranscriptFirstReadBenchmarks
{
    string _dir = null!;
    string _path = null!;
    SessionSourceOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("abbench-").FullName;
        _path = Path.Combine(_dir, "events.jsonl");
        using (var file = File.Create(_path)) { Fixtures.WriteCopilotTranscript(file, Fixtures.LargeTranscriptTurns); }
        _options = new SessionSourceOptions { CopilotHome = _dir, ClaudeHome = _dir, CodexHome = _dir };
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [Benchmark]
    public long FirstRead()
    {
        var tail = new TranscriptTail(_path, _options);
        tail.Read(new CopilotParser(new SessionState(), TimeProvider.System));
        return tail.Offset;
    }
}

// What a busy session costs each pass: one turn (about 12 KB: a prompt, a tool call and its
// output, a reply) appended, read and parsed. The append itself is in the time, a write to the
// OS's cache, and so is opening the file again just after it, which real-time scanning can make
// most of the cost. Each iteration starts again from the same transcript, so the file does not
// grow without end.
[InvocationCount(64, 1)]
public class TranscriptAppendBenchmarks
{
    const int BaseTurns = 100;
    string _dir = null!;
    string _path = null!;
    SessionSourceOptions _options = null!;
    long _baseLength;
    byte[][] _turns = null!;
    FileStream _writer = null!;
    TranscriptTail _tail = null!;
    CopilotParser _parser = null!;
    int _next;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("abbench-").FullName;
        _path = Path.Combine(_dir, "events.jsonl");
        using (var file = File.Create(_path)) { Fixtures.WriteCopilotTranscript(file, BaseTurns); }
        _baseLength = new FileInfo(_path).Length;
        _turns = [.. Enumerable.Range(BaseTurns + 1, 64).Select(Fixtures.CopilotTurnBytes)];
        _options = new SessionSourceOptions { CopilotHome = _dir, ClaudeHome = _dir, CodexHome = _dir };
        // Shared as an agent shares it: the host reads while the agent holds it open to write.
        _writer = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [IterationSetup]
    public void Rewind()
    {
        _writer.SetLength(_baseLength);
        _writer.Seek(0, SeekOrigin.End);
        _tail = new TranscriptTail(_path, _options);
        _parser = new CopilotParser(new SessionState(), TimeProvider.System);
        _tail.Read(_parser);
        _next = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Benchmark]
    public long AppendOneTurn()
    {
        _writer.Write(_turns[_next++ % _turns.Length]);
        _writer.Flush();
        _tail.Read(_parser);
        return _tail.Offset;
    }
}

// The read a reply makes before typing, to rule out a question or permission prompt holding the
// session (#599): a 21 MB events.jsonl caught up from the start, as the first reply after a start
// did (FirstRead), against one the host read ahead in the background when it found the session,
// which then reads only the turn appended since (ReadAhead).
[InvocationCount(64, 1)]
public class CopilotInputBenchmarks
{
    string _dir = null!;
    string _path = null!;
    long _baseLength;
    byte[][] _turns = null!;
    FileStream _writer = null!;
    AgentBridge.Host.Delivery.Copilot.CopilotInput _ahead = null!;
    int _next;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("abbench-").FullName;
        _path = Path.Combine(_dir, "events.jsonl");
        using (var file = File.Create(_path)) { Fixtures.WriteCopilotTranscript(file, Fixtures.LargeTranscriptTurns); }
        _baseLength = new FileInfo(_path).Length;
        _turns = [.. Enumerable.Range(Fixtures.LargeTranscriptTurns + 1, 64).Select(Fixtures.CopilotTurnBytes)];
        _writer = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [IterationSetup]
    public void Rewind()
    {
        _writer.SetLength(_baseLength);
        _writer.Seek(0, SeekOrigin.End);
        _ahead = new AgentBridge.Host.Delivery.Copilot.CopilotInput(_path);
        _ahead.Read();
        _next = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Benchmark(Baseline = true)]
    public long FirstRead() => new AgentBridge.Host.Delivery.Copilot.CopilotInput(_path).Read().ScannedTo;

    [Benchmark]
    public long ReadAhead()
    {
        _writer.Write(_turns[_next++ % _turns.Length]);
        _writer.Flush();
        return _ahead.Read().ScannedTo;
    }
}
