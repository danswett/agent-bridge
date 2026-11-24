using System.Diagnostics;
using Xunit;

namespace AgentBridge.Quality.Tests;

// build/stage-selftest-text.ps1 is the one place that decides which self-test text may leave a runner into a PUBLIC artifact (links,
// control characters, dump/zip/ELF signatures, bad JSON refused). It replaced the block pasted in ci.yml's app and app-installer jobs,
// so the move must change no behaviour: each case runs the frozen old block (build/fixtures/old-stage-block.ps1, delete at 6b) and the
// script on the same fixture and compares what each staged and whether and why each refused.
public sealed class StageSelftestTextTests
{
    static readonly string[] Names = ["installer-input.json", "installed/result.json", "installed/app.log", "app.log", "app-crash-analysis.txt"];

    record Outcome(int Code, string Reason, string Tree);

    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }
        return root;
    }

    static (int Code, string Output) Pwsh(string[] args, Dictionary<string, string>? env = null)
    {
        var start = new ProcessStartInfo("pwsh", ["-NoProfile", .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (k, v) in env ?? []) start.Environment[k] = v;
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    static (int Code, string Output) Script(string source, string output, string[] names) =>
        Pwsh(["-Command", $"& '{Path.Combine(Root(), "build", "stage-selftest-text.ps1")}' -Source '{source}' -Out '{output}' -Names {string.Join(",", names.Select(n => $"'{n}'"))}"]);

    static string Folder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"stage-{Guid.NewGuid():N}")).FullName;

    static void Put(string source, string name, byte[] bytes)
    {
        var path = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    static string Tree(string folder) => Directory.Exists(folder)
        ? string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/') + "=" + Convert.ToHexString(File.ReadAllBytes(f))).Order(StringComparer.Ordinal))
        : "<no output folder>";

    static string Reason(string output) =>
        new[] { "Linked diagnostic directory refused", "Linked diagnostic input refused", "Binary diagnostic input refused" }.FirstOrDefault(output.Contains)
        ?? (output.Length == 0 ? "" : "other");

    /// <summary>Runs the old block and the script on copies of the same fixture.</summary>
    static (Outcome Old, Outcome New) Both(Action<string> fixture)
    {
        var oldSource = Folder(); var newSource = Folder(); var oldRoot = Folder(); var newRoot = Folder();
        var oldOut = Path.Combine(oldRoot, "out"); var newOut = Path.Combine(newRoot, "out");
        try
        {
            fixture(oldSource); fixture(newSource);
            var old = Pwsh(["-File", Path.Combine(Root(), "build", "fixtures", "old-stage-block.ps1")],
                new() { ["STAGE_SOURCE"] = oldSource, ["STAGE_OUT"] = oldOut, ["STAGE_NAMES"] = string.Join(';', Names) });
            var now = Script(newSource, newOut, Names);
            return (new(old.Code == 0 ? 0 : 1, Reason(old.Output), Tree(oldOut)), new(now.Code == 0 ? 0 : 1, Reason(now.Output), Tree(newOut)));
        }
        finally { foreach (var d in new[] { oldSource, newSource, oldRoot, newRoot, oldSource + "-real", newSource + "-real" }) try { Directory.Delete(d, true); } catch (Exception) { } }
    }

    [Fact]
    public void A_clean_set_stages_the_named_files_only_and_exactly_as_the_old_block_did()
    {
        var (old, now) = Both(s =>
        {
            Put(s, "installer-input.json", Text("{\"ok\":true}"));
            Put(s, "installed/result.json", [0xEF, 0xBB, 0xBF, .. Text("{\"passed\":1}")]);
            Put(s, "installed/app.log", Text("line one\r\nline two\ttabbed\n"));
            Put(s, "app.log", Text("from the self-test"));
            Put(s, "not-named.log", Text("never staged"));
            Put(s, "installed/not-named.dmp", Text("never staged"));
        });

        Assert.Equal(old, now);
        Assert.Equal(0, now.Code);
        Assert.DoesNotContain("not-named", now.Tree);
        Assert.Equal(4, now.Tree.Split('\n').Length);
    }

    [Fact]
    public void An_empty_source_stages_nothing_and_succeeds_in_both()
    {
        var (old, now) = Both(_ => { });

        Assert.Equal(old, now);
        Assert.Equal(0, now.Code);
    }

    [Theory]
    [InlineData("MDMP\u0093§ minidump header")]
    [InlineData("PAGEDUMP and the rest")]
    [InlineData("PAGEDU64 and the rest")]
    [InlineData("PK\u0003\u0004 and the rest")]
    [InlineData("\u007fELF and the rest")]
    [InlineData("before\u0001after")]
    [InlineData("before\u0000after")]
    public void A_binary_looking_file_is_refused_with_the_old_reason_and_nothing_after_it_is_staged(string content)
    {
        var (old, now) = Both(s => { Put(s, "installer-input.json", Text("{}")); Put(s, "installed/app.log", Text(content)); Put(s, "app.log", Text("after")); });

        Assert.Equal(old, now);
        Assert.Equal((1, "Binary diagnostic input refused"), (now.Code, now.Reason));
        Assert.StartsWith("installer-input.json=", now.Tree, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", now.Tree, StringComparison.Ordinal); // only what came before the refusal; neither the refused file nor the later one
    }

    [Fact]
    public void A_json_file_that_does_not_parse_is_refused_in_both()
    {
        var (old, now) = Both(s => Put(s, "installed/result.json", Text("{ not json")));

        Assert.Equal(old, now);
        Assert.Equal(1, now.Code);
    }

    [Fact]
    public void An_empty_json_file_is_treated_exactly_as_the_old_block_treated_it()
    {
        // ConvertFrom-Json accepts an empty string; this move keeps that (tightening it is a separate change).
        var (old, now) = Both(s => Put(s, "installed/result.json", []));

        Assert.Equal(old, now);
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_refused_in_both()
    {
        var (old, now) = Both(s => Put(s, "app.log", [0xC3, 0x28, 0xFF]));

        Assert.Equal(old, now);
        Assert.Equal(1, now.Code);
    }

    [Fact]
    public void A_linked_parent_folder_is_refused_in_both()
    {
        var (old, now) = Both(s =>
        {
            var real = Directory.CreateDirectory(Path.Combine(s + "-real", "installed")).FullName;
            File.WriteAllText(Path.Combine(real, "app.log"), "through a link");
            var link = Path.Combine(s, "installed");
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, real); return; }
            Assert.Equal(0, Pwsh(["-Command", $"New-Item -ItemType Junction -Path '{link}' -Target '{real}' | Out-Null"]).Code);
        });

        Assert.Equal(old, now);
        Assert.Equal((1, "Linked diagnostic directory refused"), (now.Code, now.Reason));
    }

    [Fact]
    public void A_linked_file_is_refused_in_both()
    {
        var made = true;
        var (old, now) = Both(s =>
        {
            var real = s + "-real.txt";
            File.WriteAllText(real, "through a link");
            try { File.CreateSymbolicLink(Path.Combine(s, "app.log"), real); } catch (IOException) { made = false; } catch (UnauthorizedAccessException) { made = false; }
        });

        if (!made) return; // a file link needs a privilege some runners lack; the folder-link case above still runs
        Assert.Equal(old, now);
        Assert.Equal((1, "Linked diagnostic input refused"), (now.Code, now.Reason));
    }

    [Fact]
    public void An_existing_output_folder_is_refused_in_both_so_a_second_stage_never_mixes_two_runs()
    {
        var s = Folder(); var o = Folder();
        try
        {
            var old = Pwsh(["-File", Path.Combine(Root(), "build", "fixtures", "old-stage-block.ps1")], new() { ["STAGE_SOURCE"] = s, ["STAGE_OUT"] = o, ["STAGE_NAMES"] = "app.log" });
            var now = Script(s, o, ["app.log"]);

            Assert.Equal((1, 1), (old.Code == 0 ? 0 : 1, now.Code == 0 ? 0 : 1));
        }
        finally { Directory.Delete(s, true); Directory.Delete(o, true); }
    }
}
