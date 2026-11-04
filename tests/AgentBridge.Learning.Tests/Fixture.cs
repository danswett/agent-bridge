using System.Diagnostics;
using System.Text;

namespace AgentBridge.Learning.Tests;

static class Fixture
{
    public const string Sha = "1111111111111111111111111111111111111111";
    public const string Link = "https://example.test/review/1";
    public static readonly DateOnly Today = new(2026, 11, 3);

    public static Note Note(string id = "K-fixture-one", string status = "approved") => new(
        1, id, "gotcha", status, "fixture|caption-paint", ["src/**/*.cs"], "fixture-owner",
        Sha, ["src/caption.cs"], Today.AddDays(30), "The paint completes synchronously.", [],
        new("Wait for the observable paint with a deadline.", "Do not replace it with a fixed delay.",
            "The old caption color remains during a theme transition.", "The theme event may arrive on a later tick."),
        new Evidence
        {
            Failures = [Link],
            Diagnosis = $"The paint had not completed; see {Link}.",
            Fix = "https://example.test/commit/2",
            Verification = ["https://example.test/run/3"],
        });

    public static byte[] Trx(string outcome = "Failed", string name = "Caption_is_painted",
        string diagnostics = "", string summary = "Failed", bool definition = true) => Encoding.UTF8.GetBytes($"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="test-1" testName="Display name may contain secrets" outcome="{outcome}">
              <Output><ErrorInfo><Message>{diagnostics}</Message></ErrorInfo></Output>
            </UnitTestResult>
          </Results>
          <TestDefinitions>
            {(definition ? $"""<UnitTest id="test-1"><TestMethod className="App.Tests.CaptionTests, TestsAssembly" name="{name}" /></UnitTest>""" : "")}
          </TestDefinitions>
          <ResultSummary outcome="{summary}"><Counters total="1" {char.ToLowerInvariant(outcome[0]) + outcome[1..]}="1" /></ResultSummary>
        </TestRun>
        """);
}

sealed class ScratchRepo : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
    public string CodeCommit { get; }
    public GitSnapshot Snapshot => new(Root);

    public ScratchRepo()
    {
        Directory.CreateDirectory(Root);
        Git("init", "--quiet");
        Write("src/caption.cs", "original paint implementation");
        CodeCommit = Commit();
    }

    public string WithNotes(params Note[] notes)
    {
        foreach (var note in notes)
            Write($"{GitSnapshot.NotesFolder}/{note.Id}.json", JsonData.Write(note with { VerifiedCommit = CodeCommit }));
        return Commit();
    }

    public void Write(string path, string content)
    {
        var target = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    public string Commit()
    {
        Git("add", ".");
        Git("commit", "--quiet", "-m", "Synthetic learning fixture");
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "-c", "user.name=Learning Fixture", "-c", "user.email=fixture@example.test",
            "-c", "commit.gpgsign=false", "-c", $"core.hooksPath={Path.Combine(Root, "empty-hooks")}",
        }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.GetAwaiter().GetResult());
        return stdout;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
