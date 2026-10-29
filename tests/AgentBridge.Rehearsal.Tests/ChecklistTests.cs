using Xunit;

namespace AgentBridge.Rehearsal.Tests;

// docs/parity-checklist.md is the runbook a person or Hyperloop follows on the VM. These keep
// it honest against the tool it tells them to run and the parity.md rows it claims to cover.
public class ChecklistTests
{
    static readonly string Doc = Repo.Read("docs/parity-checklist.md");
    static readonly string Parity = Repo.Read("docs/parity.md");

    [Fact]
    public void Every_command_in_the_checklist_is_one_the_tool_accepts()
    {
        var commands = Checklist.Commands(Doc);
        Assert.True(commands.Count > 50, $"only {commands.Count} commands found");
        var refused = new List<string>();
        foreach (var command in commands)
        {
            var words = Checklist.Split(command);
            try { Cli.Parse([.. words.Skip(1)]); }
            catch (UsageException e) { refused.Add($"{command}: {e.Message}"); }
        }
        Assert.True(refused.Count == 0, string.Join("\n", refused));
    }

    [Fact]
    public void Step_ids_are_unique_and_every_step_a_command_records_is_one_the_checklist_defines()
    {
        var ids = Checklist.StepIds(Doc);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var defined = ids.ToHashSet(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var command in Checklist.Commands(Doc))
        {
            var words = Checklist.Split(command);
            var at = words.ToList().IndexOf("--step");
            if (at < 0 || at + 1 >= words.Count) { continue; }
            var step = words[at + 1];
            if (step.StartsWith('<')) { continue; }
            Assert.True(Evidence.ValidStep(step), $"{step} is not a valid step id");
            // A step may record a second check as <step>b, <step>c.
            var stem = char.IsAsciiLetterLower(step[^1]) ? step[..^1] : step;
            if (!defined.Contains(stem)) { unknown.Add(step); }
        }
        Assert.True(unknown.Count == 0, string.Join(", ", unknown));
    }

    [Fact]
    public void Every_cited_parity_row_exists_once_and_every_parity_row_is_cited()
    {
        var rows = Checklist.ParityRows(Parity);
        Assert.True(rows.Count > 100, $"only {rows.Count} parity rows found");
        var problems = new List<string>();
        var cited = new HashSet<(int, string)>();
        foreach (var (section, prefix) in Checklist.RowRefs(Doc).Distinct())
        {
            var matches = rows.Where(r => r.Section == section && r.Row.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1) { problems.Add($"§{section} \"{prefix}\" matches {matches.Count} rows"); continue; }
            cited.Add(matches[0]);
        }
        problems.AddRange(rows.Where(r => !cited.Contains(r)).Select(r => $"§{r.Section} \"{r.Row}\" is cited by no step and not listed as not exercised"));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_further_check_a_step_records_under_its_own_id_is_expected_evidence_too()
    {
        var expected = Checklist.ExpectedSteps(Doc);
        Assert.Contains("E4-06", expected);
        Assert.Contains("E4-06b", expected);
        Assert.Contains("E4-06c", expected);
        Assert.DoesNotContain("<id>", expected);
        Assert.Equal(expected.Count, expected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_command_puts_curly_quotes_inside_double_quotes_where_PowerShell_would_split_on_them()
    {
        var bad = Checklist.Commands(Doc).Where(c => System.Text.RegularExpressions.Regex.IsMatch(c, "\"[^\"]*[\u201C\u201D][^\"]*\"")).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void A_tool_name_inside_a_path_is_not_read_as_a_command_and_quoted_words_stay_whole()
    {
        Assert.Empty(Checklist.Commands("`tools/agent-bridge-rehearsal x`"));
        Assert.Equal(["agent-bridge-rehearsal", "expect", "answer", "$cp", "--text", "two words"],
            Checklist.Split("agent-bridge-rehearsal expect answer $cp --text \"two words\""));
        Assert.Equal(["agent-bridge-rehearsal sessions --agent claude --id-only"],
            Checklist.Commands("```powershell\n$cl = agent-bridge-rehearsal sessions --agent claude --id-only   # the only one\n```"));
        Assert.Equal(["agent-bridge-rehearsal note --step G-02 --result pass --detail \"PR #12\""],
            Checklist.Commands("`agent-bridge-rehearsal note --step G-02 --result pass --detail \"PR #12\"`"));
    }
}
