using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using Xunit;

// #3305 slice S1a (docs/design/script-credentials-run.md section 7): the HOST side of the scheduled-task launcher. `scripts.admit` decides, with the rule the host
// applies at agent.hello, whether the caller (the launcher) is the bound task's own running engine, and writes the audit row BEFORE it answers; the answer is one
// closed code. `scripts.finished` carries the admission id back: only from the same caller (pid and start), once, as a SECOND audit row, never an edit of the first.
public sealed class ScriptAdmissionTests : IDisposable
{
    const string Launcher = @"C:\Program Files\Slipgates\script-run.exe";
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    const string Script = @"C:\Locked\Cycle.ps1";
    const string Task = @"\Fleet\Dispatcher";
    static readonly DateTimeOffset Started = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    readonly string root = Directory.CreateTempSubdirectory("ab-admit-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    sealed class Tasks : IScriptTaskReader
    {
        public List<ScriptTaskEngine> Running { get; } = [new(4242, Started)];
        public string? Definition(string task) => new string('a', 64);
        public IReadOnlyList<string>? ActionFiles(string task) => [Launcher, Pwsh, Script];
        public IReadOnlyList<ScriptTaskEngine> RunningEngines(string task) => Running;
        public bool TaskLocked(string task) => true;
        public bool FileLocked(string path) => true;
        public string? FileSha256(string path) => new string('b', 64);
        public bool LoggedOnOnly(string task) => true;
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    // The launcher is the caller: it is the task's engine, so its own process is first in its chain.
    static ProcessAncestry Caller(int pid = 4242, string image = Launcher) =>
        new([new PeerProcess(pid, Started, image), new PeerProcess(700, Started.AddHours(-9), @"C:\Windows\System32\svchost.exe")], true);

    (ScriptAdmission Admission, AgentCredentials Store, Tasks Tasks) Open(string? bound = "dispatcher", AgentAuditLog? audit = null)
    {
        var tasks = new Tasks();
        var store = new AgentCredentials(root, "machine1", new Clock(Started), tasks: tasks, reread: pid => atReread.Processes is [var self, ..] && self.Id == pid ? atReread : ProcessAncestry.Unknown);
        if (bound is not null) { store.MintScript(bound, task: Task); }
        return (new ScriptAdmission(store, audit ?? new AgentAuditLog(root), new Clock(Started)), store, tasks);
    }

    string[] Rows(string action) => Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName))
        ? Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl").SelectMany(File.ReadAllLines).Where(r => r.Contains($"\"{action}\"")).ToArray() : [];

    // What reading the caller's chain again finds: the chain the caller presents, as the host would read it for the same process.
    ProcessAncestry atReread = Caller();
    Guid Admit(ScriptAdmission admission, string name, int version, ProcessAncestry caller) { atReread = caller; return admission.Admit(name, version, caller); }
    object Handle(ScriptAdmission admission, string op, JsonElement data, ProcessAncestry caller) { atReread = caller; return admission.Handle(op, data, caller); }

    static string Closed(Action act) => Assert.Throws<InvalidOperationException>(act).Message;

    [Fact]
    public void The_bound_tasks_own_launcher_is_admitted_and_the_audit_row_is_written_before_the_answer()
    {
        if (!OperatingSystem.IsWindows()) { return; } // the pin checks drive-letter paths, which is Windows' rule
        var (admission, _, _) = Open();
        var id = Admit(admission, "dispatcher", ScriptAdmission.Version, Caller());
        var row = Assert.Single(Rows("scripts.admit"));
        Assert.Contains(id.ToString("D"), row);
        Assert.Contains("admitted", row);
        Assert.Contains("script:machine1/dispatcher", row);
    }

    [Theory]
    [InlineData("unknown-name")]
    [InlineData("..")]
    [InlineData("a b")]
    public void A_name_with_no_live_bound_credential_is_refused_with_the_one_closed_code(string name)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, _, _) = Open();
        Assert.Equal("refused: refused", Closed(() => Admit(admission, name, ScriptAdmission.Version, Caller())));
        Assert.Empty(Rows("scripts.admit"));
    }

    [Fact]
    public void An_unbound_credential_is_never_admitted()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, store, _) = Open(bound: null);
        store.MintScript("plain");
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "plain", ScriptAdmission.Version, Caller())));
    }

    [Fact]
    public void A_caller_that_is_not_the_tasks_running_engine_is_refused_and_so_is_an_incomplete_chain()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, _, tasks) = Open();
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", ScriptAdmission.Version, Caller(pid: 9999))));
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", ScriptAdmission.Version, Caller(image: @"C:\Users\someone\script-run.exe"))));
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", ScriptAdmission.Version, Caller() with { Processes = [new PeerProcess(4242, null, Launcher)] })));
        tasks.Running.Clear();
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", ScriptAdmission.Version, Caller())));
        Assert.Empty(Rows("scripts.admit"));
    }

    [Fact]
    public void An_unknown_version_is_refused_and_an_older_one_the_host_still_speaks_is_admitted()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, _, _) = Open();
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", ScriptAdmission.Version + 1, Caller())));
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", 0, Caller())));
        Assert.NotEqual(Guid.Empty, Admit(admission, "dispatcher", 1, Caller()));
    }

    [Fact]
    public void An_unwritable_audit_refuses_and_issues_no_admission()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var blocked = Path.Combine(root, "not-a-folder");
        File.WriteAllText(blocked, "x");
        var (admission, _, _) = Open(audit: new AgentAuditLog(blocked));
        Assert.Equal("refused: refused", Closed(() => Admit(admission, "dispatcher", ScriptAdmission.Version, Caller())));
        Assert.Empty(Rows("scripts.admit"));
    }

    [Fact]
    public void Finished_is_accepted_once_from_the_admitted_caller_as_a_second_row_and_the_first_is_never_edited()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, _, _) = Open();
        var id = Admit(admission, "dispatcher", ScriptAdmission.Version, Caller());
        var first = Assert.Single(Rows("scripts.admit"));
        admission.Finished(id, 3, Caller());
        var second = Assert.Single(Rows("scripts.finished"));
        Assert.Contains(id.ToString("D"), second);
        Assert.Contains("3", second);
        Assert.Equal(first, Assert.Single(Rows("scripts.admit")));
        // Once: a repeat writes nothing.
        Assert.Equal("refused: refused", Closed(() => admission.Finished(id, 3, Caller())));
        Assert.Single(Rows("scripts.finished"));
    }

    [Fact]
    public void Finished_from_another_caller_or_for_an_unknown_id_is_refused_and_writes_nothing_and_the_real_one_still_works()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, _, _) = Open();
        var id = Admit(admission, "dispatcher", ScriptAdmission.Version, Caller());
        Assert.Equal("refused: refused", Closed(() => admission.Finished(id, 0, Caller(pid: 9999))));
        Assert.Equal("refused: refused", Closed(() => admission.Finished(id, 0, Caller() with { Processes = [new PeerProcess(4242, Started.AddSeconds(30), Launcher)] })));
        Assert.Equal("refused: refused", Closed(() => admission.Finished(Guid.NewGuid(), 0, Caller())));
        Assert.Empty(Rows("scripts.finished"));
        admission.Finished(id, 0, Caller());
        Assert.Single(Rows("scripts.finished"));
    }

    [Fact]
    public void The_wire_ops_parse_their_arguments_strictly_and_answer_the_admission_id()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (admission, _, _) = Open();
        var admitted = JsonSerializer.SerializeToElement(Handle(admission, ScriptAdmission.AdmitOp, JsonSerializer.SerializeToElement(new { name = "dispatcher", version = 1 }), Caller()));
        var id = Guid.Parse(admitted.GetProperty("admission").GetString()!);
        // The answer carries the files the host pinned, launcher first, so the launcher can refuse an argv that differs (#4524).
        Assert.Equal([Launcher, Pwsh, Script], admitted.GetProperty("files").EnumerateArray().Select(e => e.GetString()));
        foreach (var bad in new object[] { new { name = "dispatcher" }, new { name = 5, version = 1 }, new { name = "dispatcher", version = 1, extra = 1 } })
        { Assert.Equal("refused: refused", Closed(() => Handle(admission, ScriptAdmission.AdmitOp, JsonSerializer.SerializeToElement(bad), Caller()))); }
        foreach (var bad in new object[] { new { admission = id }, new { admission = "nope", exit = 0 }, new { admission = id, exit = "x" } })
        { Assert.Equal("refused: refused", Closed(() => Handle(admission, ScriptAdmission.FinishedOp, JsonSerializer.SerializeToElement(bad), Caller()))); }
        Handle(admission, ScriptAdmission.FinishedOp, JsonSerializer.SerializeToElement(new { admission = id, exit = 2 }), Caller());
        Assert.Single(Rows("scripts.finished"));
    }

    // The local pipe hands these two ops to ScriptAdmission, which is built with the host's credentials and audit log (a real read of the wiring).
    [Fact]
    public void The_local_api_routes_the_two_ops_to_the_admission_and_program_builds_one()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "AgentBridge.slnx"))) { repo = repo.Parent ?? throw new IOException("repository root not found"); }
        var api = File.ReadAllText(Path.Combine(repo.FullName, "src", "AgentBridge.Host", "LocalApi.cs"));
        var program = File.ReadAllText(Path.Combine(repo.FullName, "src", "AgentBridge.Host", "Program.cs"));
        Assert.Contains("ScriptAdmission.AdmitOp or ScriptAdmission.FinishedOp", api);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"new ScriptAdmission\("));
    }
}
