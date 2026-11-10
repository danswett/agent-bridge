using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The diagnostics journal (#737): bounded, closed, content-free. Every hostile value here is a
// made-up canary; nothing reads a real log, transcript or credential.
public sealed class DiagnosticsJournalTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
    const string Token = "ghp_CanaryToken0123456789abcdefABCDEF";
    const string Prompt = "please delete the canary prompt text";
    const string FilePath = @"C:\Users\canary-alice\secret\transcript.jsonl";
    const string Account = "canary.alice@example.invalid";
    static readonly string[] Canaries = [Token, Prompt, FilePath, "canary-alice", Account, "transcript.jsonl", ".cs", "line "];

    readonly string root = Directory.CreateTempSubdirectory("abjournal").FullName;
    readonly FakeTimeProvider time = new(Now);

    public void Dispose() => Directory.Delete(root, recursive: true);

    string File(string name = "journal.jsonl") => Path.Combine(root, name);

    static void AssertClean(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var canary in Canaries) { Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase); }
    }

    sealed class CanaryException(string message, Exception? inner = null) : Exception(message, inner);

    static Exception Thrown(Func<Exception> make)
    {
        try { throw make(); }
        catch (Exception e) { return e; }
    }

    [Fact]
    public void An_exception_is_kept_as_its_type_and_frames_never_its_message_data_inner_or_paths()
    {
        var e = Thrown(() =>
        {
            var x = new CanaryException($"{Prompt} {Token} {FilePath} {Account}", new IOException(FilePath));
            x.Data["token"] = Token;
            return x;
        });
        var journal = new DiagnosticsJournal(time, File());

        Assert.True(journal.Record(JournalEvents.Failure(JournalKind.HookSpoolDropped, e, version: $"{FilePath} {Token}")));

        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        // A test type is neither the product's nor the runtime's.
        Assert.Equal(JournalContract.Unrecognized, entry.Exception!.Type);
        Assert.True(entry.Omitted.HasFlag(JournalOmissions.ExceptionTypeUnrecognized));
        Assert.Null(entry.Version);
        Assert.True(entry.Omitted.HasFlag(JournalOmissions.VersionInvalid));
        AssertClean(JournalCodec.Write(entry));
        AssertClean(System.IO.File.ReadAllBytes(File()));
    }

    [Fact]
    public void Product_frames_are_named_by_declaring_type_and_method_only()
    {
        var unfit = JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok);
        var e = Assert.Throws<ArgumentException>(() => JournalCodec.Write(unfit));

        var shape = ExceptionShape.Project(e);

        Assert.Equal("System.ArgumentException", shape.Type);
        Assert.Contains(new JournalFrame("AgentBridge.Protocol.JournalCodec", "Write"), shape.Frames);
        Assert.All(shape.Frames, f => Assert.True(f.Type == JournalContract.External || JournalIdentifiers.IsFrame(f.Type, f.Method)));
        // The runtime's frames (xunit's, reflection's) are never named.
        Assert.DoesNotContain(shape.Frames, f => f.Type.StartsWith("System.", StringComparison.Ordinal) || f.Type.StartsWith("Xunit", StringComparison.Ordinal));
        Assert.Null(JournalIdentifiers.TypeName(typeof(CanaryException)));
    }

    // A type anyone can name System.Anything, in an assembly anyone can name AgentBridge.Anything.
    static Exception Impostor()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AgentBridge.Host.Core"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("AgentBridge.Host.Core")
            .DefineType("System.IO.CanaryAliceGhpTokenException", TypeAttributes.Public, typeof(IOException));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        return (Exception)Activator.CreateInstance(type.CreateType())!;
    }

    [Fact]
    public void A_name_that_only_looks_like_the_runtimes_or_the_products_is_never_kept()
    {
        var impostor = Thrown(Impostor);
        Assert.Equal(JournalContract.Unrecognized, ExceptionShape.Project(impostor).Type);

        var journal = new DiagnosticsJournal(time);
        var lookalike = new JournalException("System.IO.CanaryAliceGhpTokenException", [
            new JournalFrame("System.Security.CanaryAlice", "ReadGhpToken"),
            new JournalFrame("AgentBridge.Host.Core.Intake.HookIntake", "CanaryAliceTranscript"),
            new JournalFrame("AgentBridge.Host.Core.Intake.CanaryAlice", "Pass"),
        ]);
        Assert.True(journal.Record(JournalContract.Draft(JournalKind.UnhandledException, JournalOutcome.Failed, exception: lookalike)));
        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Equal(JournalContract.Unrecognized, entry.Exception!.Type);
        Assert.Equal(new[] { new JournalFrame(JournalContract.External, JournalContract.External) }, entry.Exception.Frames);
        Assert.DoesNotContain("Canary", Encoding.UTF8.GetString(JournalCodec.Write(entry)), StringComparison.OrdinalIgnoreCase);

        // Built directly, as kept, the same names are not a record at all.
        var crafted = entry with { Exception = lookalike, Omitted = JournalOmissions.None };
        Assert.False(JournalContract.IsFit(crafted));
        Assert.Throws<ArgumentException>(() => JournalCodec.Write(crafted));
        var real = new JournalFrame("AgentBridge.Host.Core.Intake.HookIntake", "Pass");
        Assert.True(JournalContract.IsFit(entry with { Exception = new JournalException("System.IO.IOException", [real]), Omitted = JournalOmissions.None }));
        Assert.False(JournalContract.IsFit(entry with { Exception = new JournalException("System.IO.IOException", [real with { Method = "Canary" }]), Omitted = JournalOmissions.None }));
    }

    [Fact]
    public void Hostile_strings_in_a_draft_are_dropped_and_marked_before_anything_is_kept()
    {
        var journal = new DiagnosticsJournal(time);
        var draft = JournalContract.Draft(JournalKind.RelayClosed, JournalOutcome.Failed, code: 70000, version: Prompt, durationMs: -5,
            stages: [new JournalStage(JournalStageName.Connect, 12), new JournalStage((JournalStageName)99, 1)],
            exception: new JournalException(FilePath, [
                new JournalFrame("System.IO.File", "ReadAllText"),
                new JournalFrame($"System.{Token} {FilePath}", "Read"),
                new JournalFrame("AgentBridge.Host.Core.Intake.HookIntake", $"Pass {Prompt}"),
                new JournalFrame("Evil.Module", "Run"),
            ]));

        Assert.True(journal.Record(draft));

        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Null(entry.Code);
        Assert.Null(entry.Version);
        Assert.Equal(0, entry.DurationMs);
        Assert.Equal(new[] { new JournalStage(JournalStageName.Connect, 12) }, entry.Stages);
        Assert.Equal(JournalContract.Unrecognized, entry.Exception!.Type);
        // None of the four is the product's own: they read as one frame outside it.
        Assert.Equal(new[] { new JournalFrame(JournalContract.External, JournalContract.External) }, entry.Exception.Frames);
        Assert.Equal(
            JournalOmissions.CodeOutOfRange | JournalOmissions.VersionInvalid | JournalOmissions.DurationClamped
                | JournalOmissions.StagesTruncated | JournalOmissions.ExceptionTypeUnrecognized | JournalOmissions.FrameUnrecognized,
            entry.Omitted);
        AssertClean(JournalCodec.Write(entry));
    }

    [Fact]
    public void A_kind_or_outcome_that_is_not_the_journals_is_refused_and_counted()
    {
        var journal = new DiagnosticsJournal(time);

        Assert.False(journal.Record(JournalContract.Draft(JournalKind.HostStopped, JournalOutcome.Ok) with { Kind = (JournalKind)999 }));
        Assert.False(journal.Record(JournalContract.Draft(JournalKind.HostStopped, (JournalOutcome)42)));

        var snapshot = journal.Read(10, JournalContract.MaxTotalBytes);
        Assert.Empty(snapshot.Entries);
        Assert.Equal(2, snapshot.RejectedMalformed);
    }

    static string Line(string body) => "{\"schema\":1,\"seq\":1,\"at\":\"2026-11-04T12:00:00.000Z\"," + body + "}";

    public static TheoryData<string> Unreadable => new()
    {
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"message\":\"" + Prompt + "\""),
        Line("\"family\":7,\"kind\":\"UnhandledException\",\"outcome\":\"Failed\""),
        Line("\"family\":\"Crash\",\"kind\":\"Prompt\",\"outcome\":\"Failed\""),
        Line("\"family\":\"crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed, Ok\""),
        Line("\"family\":\"Crash\",\"kind\":\"HostStarted\",\"outcome\":\"Ok\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"outcome\":\"Ok\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"" + FilePath.Replace(@"\", @"\\") + "\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception\",\"frames\":[{\"type\":\"System.X\",\"method\":\"Y\",\"file\":\"a.cs\"}]}"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception\",\"frames\":[{\"type\":\"Evil.X\",\"method\":\"Y\"}]}"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception\",\"frames\":[{\"type\":\"System.IO.File\",\"method\":\"ReadAllText\"}]}"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.IO.CanaryAliceGhpTokenException\",\"frames\":[]}"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"1.2.3-canaryalice\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"1.2.3\\n\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception " + Token + "\",\"frames\":[]}"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"omitted\":0"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"omitted\":1024"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"code\":\"1006\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"ms\":-1"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"extra\":{}"),
        "{\"schema\":2,\"seq\":1,\"at\":\"2026-11-04T12:00:00.000Z\",\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"}",
        "{\"schema\":1,\"seq\":0,\"at\":\"2026-11-04T12:00:00.000Z\",\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"}",
        "{\"schema\":1,\"seq\":1,\"at\":\"2026-11-04T12:00:00+02:00\",\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"}",
        "{\"schema\":1,\"seq\":1,\"at\":\"2026-11-04T12:00:00.000Z\",\"family\":\"Crash\",\"kind\":\"UnhandledException\"}",
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"") + "x",
        "[" + Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"") + "]",
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"1.0.0" + new string('0', 3000) + "\""),
        // Escapes that parse but decode to no UTF-16 text.
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"\\uD800\""),
        Line("\"family\":\"Crash\\uDC00\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\""),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception\\uD800\",\"frames\":[]}"),
        Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception\",\"frames\":[{\"type\":\"System.X\",\"method\":\"\\uDBFF\"}]}"),
        "{\"schema\":1,\"seq\":1,\"at\":\"2026-11-04T12:00:00.000\\uD800\",\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"}",
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void A_record_that_is_not_exactly_the_contracts_is_not_read(string line)
    {
        Assert.True(JournalCodec.TryRead(Encoding.UTF8.GetBytes(Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\"")), out _));

        Assert.False(JournalCodec.TryRead(Encoding.UTF8.GetBytes(line), out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Another_process_names_it_does_not_know_read_as_outside_the_product_and_nothing_else_is_forgiven()
    {
        var known = Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.IO.IOException\",\"frames\":["
            + "{\"type\":\"AgentBridge.Host.Core.Intake.HookIntake\",\"method\":\"Pass\"},"
            + "{\"type\":\"AgentBridge.Host.Program\",\"method\":\"Main\"},"
            + "{\"type\":\"System.CanaryAlice\",\"method\":\"" + Token + "\"}]}");
        Assert.False(JournalCodec.TryRead(Encoding.UTF8.GetBytes(known), out _));

        Assert.True(JournalCodec.TryReadFromAnotherProcess(Encoding.UTF8.GetBytes(known), out var entry));
        Assert.Equal("System.IO.IOException", entry!.Exception!.Type);
        Assert.Equal(new[]
        {
            new JournalFrame("AgentBridge.Host.Core.Intake.HookIntake", "Pass"),
            new JournalFrame(JournalContract.External, JournalContract.External),
        }, entry.Exception.Frames);
        Assert.Equal(JournalOmissions.FrameUnrecognized, entry.Omitted);
        AssertClean(JournalCodec.Write(entry));

        var oddType = Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.IO.CanaryAliceException\",\"frames\":[]}");
        Assert.True(JournalCodec.TryReadFromAnotherProcess(Encoding.UTF8.GetBytes(oddType), out var typed));
        Assert.Equal(JournalContract.Unrecognized, typed!.Exception!.Type);
        Assert.Equal(JournalOmissions.ExceptionTypeUnrecognized, typed.Omitted);
        Assert.DoesNotContain("Canary", Encoding.UTF8.GetString(JournalCodec.Write(typed)), StringComparison.OrdinalIgnoreCase);
        // The sentinel itself is not a type: it keeps its omission, as the record it came from said.
        var sentinel = Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"<unrecognized>\",\"frames\":[]}");
        Assert.True(JournalCodec.TryReadFromAnotherProcess(Encoding.UTF8.GetBytes(sentinel), out var marked));
        Assert.Equal(JournalContract.Unrecognized, marked!.Exception!.Type);
        Assert.Equal(JournalOmissions.ExceptionTypeUnrecognized, marked.Omitted);

        // Only names are forgiven: a bad version, unknown property or bit is still not a record.
        foreach (var line in new[]
        {
            Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"1.2.3-canaryalice\""),
            Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"message\":\"" + Prompt + "\""),
            Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"omitted\":128"),
            Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"exception\":{\"type\":\"System.Exception\",\"frames\":[{\"type\":\"System.X\",\"method\":\"Y\",\"file\":\"a.cs\"}]}"),
        })
        {
            Assert.False(JournalCodec.TryReadFromAnotherProcess(Encoding.UTF8.GetBytes(line), out var refused));
            Assert.Null(refused);
        }
    }

    [Fact]
    public async Task A_read_waiting_for_the_journal_can_be_cancelled()
    {
        var journal = new DiagnosticsJournal(time);
        journal.Record(JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await journal.ReadAsync(10, JournalContract.MaxTotalBytes, cancel.Token));
        Assert.Single((await journal.ReadAsync(10, JournalContract.MaxTotalBytes, CancellationToken.None)).Entries);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await journal.ReadAsync(-1, 10, CancellationToken.None));
    }

    [Fact]
    public void The_ring_keeps_the_last_500_and_reads_newest_first_within_the_readers_limits()
    {
        var journal = new DiagnosticsJournal(time);
        for (var i = 0; i < JournalContract.MaxEntries + 3; i++)
        {
            Assert.True(journal.Record(JournalContract.Draft(JournalKind.DeliveryStage, JournalOutcome.Ok, durationMs: i)));
        }

        var all = journal.Read(int.MaxValue, int.MaxValue);
        Assert.Equal(JournalContract.MaxEntries, all.Entries.Count);
        Assert.Equal(3, all.EvictedByCount);
        Assert.Equal(JournalContract.MaxEntries + 2, all.Entries[0].DurationMs);
        Assert.Equal(JournalContract.MaxEntries + 3, all.Entries[0].Seq);

        var few = journal.Read(10, int.MaxValue);
        Assert.Equal(10, few.Entries.Count);
        Assert.Equal(JournalContract.MaxEntries - 10, few.OmittedByLimit);
        var small = journal.Read(int.MaxValue, 200);
        Assert.True(small.Entries.Sum(e => JournalCodec.Write(e).Length) <= 200);
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.Read(-1, 10));
    }

    [Fact]
    public void Records_older_than_a_day_age_out()
    {
        var journal = new DiagnosticsJournal(time);
        journal.Record(JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok));
        time.Advance(TimeSpan.FromHours(23));
        journal.Record(JournalContract.Draft(JournalKind.HostStopped, JournalOutcome.Ok));
        time.Advance(TimeSpan.FromHours(2));

        var snapshot = journal.Read(10, JournalContract.MaxTotalBytes);

        Assert.Equal(JournalKind.HostStopped, Assert.Single(snapshot.Entries).Kind);
        Assert.Equal(1, snapshot.EvictedByAge);
    }

    // An assembly the test trusts, holding types and methods with names near the longest a record
    // keeps, so that all sixteen frames cannot fit one record.
    static readonly IReadOnlyList<JournalFrame> LongFrames = TrustedLongFrames();

    static IReadOnlyList<JournalFrame> TrustedLongFrames()
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LongFrames"), AssemblyBuilderAccess.Run).DefineDynamicModule("LongFrames");
        var methods = new List<MethodInfo>();
        for (var i = 0; i < JournalContract.MaxFrames; i++)
        {
            var type = module.DefineType("LongFrames." + new string('T', 100) + i, TypeAttributes.Public);
            type.DefineMethod("M" + new string('m', 100) + i, MethodAttributes.Public | MethodAttributes.Static).GetILGenerator().Emit(OpCodes.Ret);
            methods.Add(type.CreateType().GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Single());
        }
        JournalIdentifiers.Trust(methods[0].DeclaringType!.Assembly);
        return methods.Select(m => JournalIdentifiers.FrameOf(m) ?? throw new InvalidOperationException("not trusted")).ToList();
    }

    static JournalEntry Large() => JournalContract.Draft(JournalKind.UnhandledException, JournalOutcome.Failed,
        exception: new JournalException("System.Exception", LongFrames));

    [Fact]
    public void A_record_too_long_loses_its_last_frames_and_the_ring_stays_within_its_bytes()
    {
        var journal = new DiagnosticsJournal(time, File());
        for (var i = 0; i < 300; i++) { Assert.True(journal.Record(Large())); }

        var snapshot = journal.Read(int.MaxValue, int.MaxValue);
        var entry = snapshot.Entries[0];
        Assert.True(entry.Omitted.HasFlag(JournalOmissions.FramesTruncated));
        Assert.InRange(entry.Exception!.Frames.Count, 1, JournalContract.MaxFrames - 1);
        Assert.All(snapshot.Entries, e => Assert.InRange(JournalCodec.Write(e).Length, 1, JournalContract.MaxEntryBytes));
        Assert.True(snapshot.Entries.Sum(e => JournalCodec.Write(e).Length) <= JournalContract.MaxTotalBytes);
        Assert.True(snapshot.EvictedByBytes > 0);
        Assert.InRange(new FileInfo(File()).Length, 1, DiagnosticsJournal.MaxFileBytes);
    }

    [Fact]
    public void The_file_is_read_back_strictly_and_rewritten_without_what_could_not_be_read()
    {
        var first = new DiagnosticsJournal(time, File());
        first.Record(JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok, version: "1.2.3"));
        first.Record(JournalContract.Draft(JournalKind.RelayClosed, JournalOutcome.Failed, code: 1006));
        System.IO.File.AppendAllText(File(),
            Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"message\":\"" + Token + "\"").Replace("\"seq\":1", "\"seq\":9") + "\n"
            + "{\"schema\":1,\"seq\":10,\"at\":\"2026-11-04T12:00:00.000Z\",\"fam\n"
            + Line("\"family\":\"HostLifecycle\",\"kind\":\"HostStopped\",\"outcome\":\"Ok\"") + "\n");

        time.Advance(TimeSpan.FromMinutes(1));
        var second = new DiagnosticsJournal(time, File());
        second.Record(JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok));

        var snapshot = second.Read(10, JournalContract.MaxTotalBytes);
        Assert.Equal(new[] { 3L, 2L, 1L }, snapshot.Entries.Select(e => e.Seq));
        Assert.Equal(1006, snapshot.Entries[1].Code);
        Assert.Equal("1.2.3", snapshot.Entries[2].Version);
        // The canary line, the cut-short line and the one out of sequence.
        Assert.Equal(3, snapshot.DroppedOnLoad);
        Assert.False(snapshot.LoadFailed);
        AssertClean(System.IO.File.ReadAllBytes(File()));
        Assert.Equal(3, System.IO.File.ReadAllLines(File()).Length);
    }

    [Fact]
    public void A_line_that_cannot_be_decoded_is_dropped_at_start_and_the_journal_still_loads()
    {
        var first = new DiagnosticsJournal(time, File());
        first.Record(JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok, version: "1.2.3"));
        var surrogate = Line("\"family\":\"Crash\",\"kind\":\"UnhandledException\",\"outcome\":\"Failed\",\"version\":\"\\uD800\"").Replace("\"seq\":1", "\"seq\":2");
        Assert.False(JournalCodec.TryReadFromAnotherProcess(Encoding.UTF8.GetBytes(surrogate), out _));
        System.IO.File.AppendAllText(File(), surrogate + "\n");

        time.Advance(TimeSpan.FromMinutes(1));
        var second = new DiagnosticsJournal(time, File());

        var snapshot = second.Read(10, JournalContract.MaxTotalBytes);
        Assert.Equal(new[] { 1L }, snapshot.Entries.Select(e => e.Seq));
        Assert.Equal(1, snapshot.DroppedOnLoad);
        Assert.False(snapshot.LoadFailed);
        Assert.DoesNotContain("uD800", System.IO.File.ReadAllText(File()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_that_cannot_be_written_leaves_the_journal_in_memory_and_says_so()
    {
        Directory.CreateDirectory(File());
        var journal = new DiagnosticsJournal(time, File());

        Assert.True(journal.Record(JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok)));

        var snapshot = journal.Read(10, JournalContract.MaxTotalBytes);
        Assert.Single(snapshot.Entries);
        Assert.True(snapshot.PersistFailures > 0);
    }

    [Fact]
    public void The_journal_has_no_way_to_send_anything()
    {
        var types = new[] { typeof(DiagnosticsJournal), typeof(JournalCodec), typeof(JournalContract), typeof(ExceptionShape), typeof(JournalEvents) };
        foreach (var t in types)
        {
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var m in members)
            {
                IEnumerable<Type> used = m switch
                {
                    FieldInfo f => new[] { f.FieldType },
                    MethodInfo mi => mi.GetParameters().Select(p => p.ParameterType).Append(mi.ReturnType),
                    ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
                    _ => Array.Empty<Type>(),
                };
                Assert.All(used, u => Assert.False(u.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true, $"{t.Name}.{m.Name} uses {u}"));
            }
        }
    }

    [Fact]
    public void A_relay_connect_is_kept_as_an_outcome_and_a_duration_only()
    {
        var journal = new DiagnosticsJournal(time, File());

        Assert.True(journal.Record(JournalEvents.RelayConnected(TimeSpan.FromMilliseconds(250), "1.2.3")));

        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Equal(JournalKind.RelayConnected, entry.Kind);
        Assert.Equal(JournalOutcome.Ok, entry.Outcome);
        Assert.Equal(250, entry.DurationMs);
        Assert.Null(entry.Exception);
        Assert.Equal(JournalOmissions.None, entry.Omitted);
    }

    [Fact]
    public void A_refused_connect_keeps_the_close_code_and_no_exception_text()
    {
        var journal = new DiagnosticsJournal(time, File());
        var refused = Thrown(() => new InvalidOperationException($"relay closed the connection: 4001 {Token} https://relay.canary.invalid/{Prompt}"));

        Assert.True(journal.Record(JournalEvents.RelayConnectFailed(refused, 4001, TimeSpan.FromSeconds(1))));

        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Equal((JournalKind.RelayConnectFailed, JournalOutcome.Refused, 4001), (entry.Kind, entry.Outcome, entry.Code));
        Assert.Null(entry.Exception);
        AssertClean(System.IO.File.ReadAllBytes(File()));
        Assert.DoesNotContain("relay.canary", Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(File())), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_connect_stores_the_exception_type_and_never_its_message_even_with_a_secret_in_it()
    {
        var journal = new DiagnosticsJournal(time, File());
        var e = Thrown(() => new CanaryException($"{Token} {Account} wss://relay.canary.invalid/ws?token={Token}"));

        Assert.True(journal.Record(JournalEvents.RelayConnectFailed(e, null, TimeSpan.FromMilliseconds(40))));

        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Equal(JournalOutcome.Failed, entry.Outcome);
        Assert.Null(entry.Code);
        Assert.Equal(JournalContract.Unrecognized, entry.Exception!.Type);
        var stored = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(File()));
        Assert.DoesNotContain("canary.invalid", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wss://", stored, StringComparison.OrdinalIgnoreCase);
        AssertClean(System.IO.File.ReadAllBytes(File()));
    }

    [Fact]
    public void A_cancelled_connect_is_cancelled_with_no_exception()
    {
        var entry = JournalEvents.RelayConnectFailed(new OperationCanceledException(Token), null, TimeSpan.Zero);

        Assert.Equal(JournalOutcome.Cancelled, entry.Outcome);
        Assert.Null(entry.Exception);
        Assert.NotNull(JournalContract.Fit(entry));
    }

    [Theory]
    [InlineData(1000, JournalOutcome.Ok)]
    [InlineData(4009, JournalOutcome.Refused)]
    [InlineData(1012, JournalOutcome.Unavailable)]
    [InlineData(null, JournalOutcome.Unavailable)]
    public void A_relay_close_keeps_its_status_as_the_code_and_how_long_it_was_connected(int? status, JournalOutcome outcome)
    {
        var journal = new DiagnosticsJournal(time, File());

        Assert.True(journal.Record(JournalEvents.RelayClosed(status, TimeSpan.FromMinutes(3))));

        var entry = Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Equal((JournalKind.RelayClosed, JournalFamily.RelayDrop, outcome, status, 180_000L), (entry.Kind, entry.Family, entry.Outcome, entry.Code, entry.DurationMs));
        Assert.True(JournalContract.IsFit(entry));
    }
}
