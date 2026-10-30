using System.IO.Compression;
using System.Text;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// A session's transcript packed on one machine and installed on another as a session of its
// own (SessionBundles), as agent-ha-bridge's bundle and install did: only what makes the session,
// never through a link, renamed throughout, and a bundle that is not a session refused with
// nothing left behind.
public sealed class SessionBundleTests : IDisposable
{
    const string Id = "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6";
    const string NewId = "11111111-2222-4333-8444-555555555555";
    static readonly DateTimeOffset Now = new(2026, 10, 29, 21, 0, 0, TimeSpan.Zero);

    readonly string root = Directory.CreateTempSubdirectory("abt-").FullName;
    readonly AgentHomes source;
    readonly AgentHomes target;

    public SessionBundleTests()
    {
        source = Homes("source");
        target = Homes("target");
    }

    AgentHomes Homes(string name) => new(Path.Combine(root, name, ".copilot"), Path.Combine(root, name, ".claude"), Path.Combine(root, name, ".codex"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    string Zip => Path.Combine(root, "bundle.zip");
    string Unpack => Path.Combine(root, "unpack");

    string CopilotSession()
    {
        var dir = Directory.CreateDirectory(Path.Combine(source.Copilot, "session-state", Id)).FullName;
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), $"{{\"type\":\"session.start\",\"data\":{{\"sessionId\":\"{Id}\"}}}}\n{{\"type\":\"user.message\",\"data\":{{\"content\":\"Fix the flaky test\"}}}}\n");
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), $"id: {Id}\ncwd: 'C:\\Users\\rezna\\repos\\app'\nsummary: Fix the flaky test\n");
        Directory.CreateDirectory(Path.Combine(dir, "checkpoints"));
        File.WriteAllText(Path.Combine(dir, "checkpoints", "001-plan.md"), $"Plan for {Id}");
        Directory.CreateDirectory(Path.Combine(dir, "files", "notes"));
        File.WriteAllText(Path.Combine(dir, "files", "notes", "design.md"), "saved to files\\notes");
        // This machine's undo state and database: they describe its own disks, and stay.
        Directory.CreateDirectory(Path.Combine(dir, "rewind-snapshots"));
        File.WriteAllText(Path.Combine(dir, "rewind-snapshots", "1.bin"), "x");
        File.WriteAllText(Path.Combine(dir, "session.db"), "x");
        return dir;
    }

    [Fact]
    public void A_Copilot_session_moves_with_its_checkpoints_and_files_and_arrives_as_a_session_of_its_own()
    {
        var dir = CopilotSession();
        var before = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        var packed = SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, long.MaxValue);
        using (var z = ZipFile.OpenRead(Zip))
        {
            Assert.Equal(["checkpoints/001-plan.md", "events.jsonl", "files/notes/design.md", "workspace.yaml"], z.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        Assert.Equal((4, new FileInfo(Zip).Length), (packed.Files, packed.Bytes));
        // Read-only at the source.
        Assert.Equal(before, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText));

        var cwd = Path.Combine(root, "target", "repos", "app");
        var transcript = SessionBundles.Install(Zip, AgentKind.Copilot, Id, NewId, cwd, target, Unpack, long.MaxValue, Now);

        var installed = Path.Combine(target.Copilot, "session-state", NewId);
        Assert.Equal(Path.Combine(installed, "events.jsonl"), transcript);
        Assert.Contains($"\"sessionId\":\"{NewId}\"", File.ReadAllText(transcript));
        Assert.DoesNotContain(Id, File.ReadAllText(transcript));
        Assert.Contains($"cwd: '{cwd}'", File.ReadAllText(Path.Combine(installed, "workspace.yaml")));
        Assert.Equal($"Plan for {NewId}", File.ReadAllText(Path.Combine(installed, "checkpoints", "001-plan.md")));
        Assert.Equal("saved to files\\notes", File.ReadAllText(Path.Combine(installed, "files", "notes", "design.md")));
        Assert.False(Directory.Exists(Unpack));
        Assert.True(SessionBundles.Exists(AgentKind.Copilot, target, NewId));
    }

    [Fact]
    public void A_link_inside_a_session_is_never_followed_into_the_bundle()
    {
        var dir = CopilotSession();
        var secrets = Directory.CreateDirectory(Path.Combine(root, "secrets")).FullName;
        File.WriteAllText(Path.Combine(secrets, "id_rsa"), "PRIVATE");
        var link = Path.Combine(dir, "files", "repo");
        if (OperatingSystem.IsWindows())
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, secrets]) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
        }
        else { Directory.CreateSymbolicLink(link, secrets); }
        Assert.True(File.Exists(Path.Combine(link, "id_rsa")));

        SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, long.MaxValue);

        using var z = ZipFile.OpenRead(Zip);
        Assert.DoesNotContain(z.Entries, e => e.FullName.Contains("id_rsa", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_and_Codex_sessions_keep_the_names_their_agents_find_them_by_and_are_listed_in_the_folder_they_open_in()
    {
        const string There = @"C:\\Users\\rezna\\repos\\app";
        var claude = Directory.CreateDirectory(Path.Combine(source.Claude, "projects", "C--Users-rezna-repos-app")).FullName;
        File.WriteAllText(Path.Combine(claude, $"{Id}.jsonl"),
            $"{{\"type\":\"user\",\"sessionId\":\"{Id}\",\"cwd\":\"{There}\",\"message\":{{\"role\":\"user\",\"content\":\"Fix the flaky test ✓\"}}}}\n"
            + $"{{\"type\":\"assistant\",\"sessionId\":\"{Id}\",\"cwd\":\"{There}\",\"message\":{{\"content\":[]}},\"n\":1.50}}\n"
            + "not json, \"cwd\" or not\n");
        SessionBundles.Pack(AgentKind.Claude, source, Id, Zip, long.MaxValue);
        var cwd = Path.Combine(root, "target", "repos", "app");
        var installed = SessionBundles.Install(Zip, AgentKind.Claude, Id, NewId, cwd, target, Unpack, long.MaxValue, Now);
        Assert.Equal($"{NewId}.jsonl", Path.GetFileName(installed));
        Assert.Equal(Path.Combine(target.Claude, "projects"), Path.GetDirectoryName(Path.GetDirectoryName(installed)));
        var lines = File.ReadAllLines(installed);
        Assert.DoesNotContain(lines, l => l.Contains("rezna", StringComparison.Ordinal));
        Assert.Contains("Fix the flaky test ✓", lines[0]);
        Assert.Contains("\"n\":1.50", lines[1]);
        Assert.Equal("not json, \"cwd\" or not", lines[2]);
        File.Delete(Zip);

        var codex = Directory.CreateDirectory(Path.Combine(source.Codex, "sessions", "2026", "10", "01")).FullName;
        File.WriteAllText(Path.Combine(codex, $"rollout-2026-10-01T09-00-00-{Id}.jsonl"),
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{Id}\",\"cwd\":\"{There}\"}}}}\n"
            + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Profile the relay\"}}\n"
            + $"{{\"type\":\"turn_context\",\"payload\":{{\"cwd\":\"{There}\"}}}}\n"
            + $"{{\"type\":\"response_item\",\"payload\":{{\"output\":\"ran in {There}\",\"cwd\":\"{There}\"}}}}\n");
        SessionBundles.Pack(AgentKind.Codex, source, Id, Zip, long.MaxValue);
        installed = SessionBundles.Install(Zip, AgentKind.Codex, Id, NewId, cwd, target, Unpack, long.MaxValue, Now);
        Assert.Equal(Path.Combine(target.Codex, "sessions", "2026", "10", "29", $"rollout-2026-10-29T21-00-00-{NewId}.jsonl"), installed);
        lines = File.ReadAllLines(installed);
        Assert.Contains($"\"id\":\"{NewId}\"", lines[0]);
        Assert.DoesNotContain("rezna", lines[0] + lines[2]);
        // Only the fields Codex reads its folder from: what a tool printed stays as it was.
        Assert.Contains("rezna", lines[3]);

        // So this machine lists each where it now opens, and an approved folder offers it.
        var listed = new ResumableStore(target.Copilot, target.Claude, target.Codex).Read(["claude", "codex"]);
        Assert.Equal([(AgentKind.Claude, cwd), (AgentKind.Codex, cwd)], listed.Where(e => e.Id == NewId).Select(e => (e.Agent, e.Folder)).Order().ToArray());
    }

    [Fact]
    public void A_session_too_large_to_read_or_missing_its_transcript_is_not_packed()
    {
        var dir = CopilotSession();
        Assert.StartsWith("that session is 1 KiB on disk, over the 1 KiB a transfer reads", Assert.Throws<InvalidOperationException>(() => SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, 100)).Message);
        Assert.False(File.Exists(Zip));
        Assert.Equal("that session has 4 files, over the 3 a transfer takes", Assert.Throws<InvalidOperationException>(() => SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, long.MaxValue, maxFiles: 3)).Message);
        File.Delete(Path.Combine(dir, "events.jsonl"));
        Assert.Equal("that session's files are not on this machine", Assert.Throws<InvalidOperationException>(() => SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, long.MaxValue)).Message);
    }

    static void Bundle(string zip, params (string Name, string Text)[] entries)
    {
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
    }

    [Theory]
    [InlineData("../escape.jsonl", "the bundle holds '../escape.jsonl', which is not part of a Copilot session")]
    [InlineData("files/../../escape.txt", "the bundle holds 'files/../../escape.txt', which is not part of a Copilot session")]
    [InlineData("/etc/passwd", "the bundle holds '/etc/passwd', which is not part of a Copilot session")]
    [InlineData("C:/Windows/evil.dll", "the bundle holds 'C:/Windows/evil.dll', which is not part of a Copilot session")]
    [InlineData("rewind-snapshots/1.bin", "the bundle holds 'rewind-snapshots/1.bin', which is not part of a Copilot session")]
    public void A_bundle_naming_anything_outside_a_session_is_refused_and_nothing_is_installed(string name, string problem)
    {
        Bundle(Zip, ("events.jsonl", "{}\n"), (name, "x"));

        var e = Assert.Throws<InvalidDataException>(() => SessionBundles.Install(Zip, AgentKind.Copilot, Id, NewId, root, target, Unpack, long.MaxValue, Now));

        Assert.Equal(problem, e.Message);
        Assert.False(Directory.Exists(Path.Combine(target.Copilot, "session-state", NewId)));
        Assert.False(Directory.Exists(Unpack));
        Assert.False(File.Exists(Path.Combine(root, "escape.jsonl")));
    }

    [Fact]
    public void A_bundle_with_no_transcript_or_that_unpacks_past_the_limit_installs_nothing()
    {
        // Copilot handed an id with no transcript starts an empty session and exits 0.
        Bundle(Zip, ("workspace.yaml", "id: x\n"));
        Assert.Equal("the bundle holds no Copilot transcript", Assert.Throws<InvalidDataException>(() => SessionBundles.Install(Zip, AgentKind.Copilot, Id, NewId, root, target, Unpack, long.MaxValue, Now)).Message);
        File.Delete(Zip);

        Bundle(Zip, ("events.jsonl", new string('x', 4096)));
        Assert.Equal("the bundle unpacks to more than 1 KiB", Assert.Throws<InvalidDataException>(() => SessionBundles.Install(Zip, AgentKind.Copilot, Id, NewId, root, target, Unpack, 1024, Now)).Message);
        File.Delete(Zip);

        Bundle(Zip, ("a.jsonl", "{}"), ("b.jsonl", "{}"));
        Assert.Equal("the bundle holds no Claude transcript", Assert.Throws<InvalidDataException>(() => SessionBundles.Install(Zip, AgentKind.Claude, Id, NewId, root, target, Unpack, long.MaxValue, Now)).Message);

        Assert.False(Directory.Exists(Path.Combine(target.Copilot, "session-state", NewId)));
        Assert.False(Directory.Exists(Path.Combine(target.Claude, "projects")) && Directory.EnumerateFiles(Path.Combine(target.Claude, "projects"), "*", SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(Unpack));
    }

    [Fact]
    public void A_transcript_line_too_long_to_rewrite_safely_is_refused_and_nothing_is_installed()
    {
        Bundle(Zip, ($"{Id}.jsonl", "{\"type\":\"user\"}\n" + new string('x', SessionBundles.MaxLine + 1) + "\n"));
        Assert.Equal("the bundle holds a line over 16 MiB, which this machine does not rewrite",
            Assert.Throws<InvalidDataException>(() => SessionBundles.Install(Zip, AgentKind.Claude, Id, NewId, root, target, Unpack, long.MaxValue, Now)).Message);
        Assert.False(Directory.Exists(Path.Combine(target.Claude, "projects")) && Directory.EnumerateFiles(Path.Combine(target.Claude, "projects"), "*", SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(Unpack));
    }

    [Fact]
    public void A_saved_file_named_like_a_rewrite_in_progress_arrives_intact_beside_its_namesake()
    {
        var dir = CopilotSession();
        File.WriteAllText(Path.Combine(dir, "files", "notes", "design.md.rename"), "a file of its own");
        SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, long.MaxValue);
        SessionBundles.Install(Zip, AgentKind.Copilot, Id, NewId, root, target, Unpack, long.MaxValue, Now);
        var notes = Path.Combine(target.Copilot, "session-state", NewId, "files", "notes");
        Assert.Equal("saved to files\\notes", File.ReadAllText(Path.Combine(notes, "design.md")));
        Assert.Equal("a file of its own", File.ReadAllText(Path.Combine(notes, "design.md.rename")));
        Assert.Equal(["design.md", "design.md.rename"], Directory.GetFiles(notes).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void Packing_stops_when_its_deadline_passes_and_leaves_no_bundle()
    {
        CopilotSession();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SessionBundles.Pack(AgentKind.Copilot, source, Id, Zip, long.MaxValue, cancellationToken: cancelled.Token));
        Assert.False(File.Exists(Zip));
    }
}
