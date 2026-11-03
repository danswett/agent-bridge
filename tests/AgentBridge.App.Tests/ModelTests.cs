using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class MarkdownTests
{
    [Fact]
    public void Headings_paragraphs_emphasis_and_code_become_styled_runs()
    {
        var blocks = MarkdownModel.Parse("# Done\n\nAll **green** and *quick*, see `ci.yml`.\n\n```powershell\ndotnet test\n```");

        var heading = Assert.IsType<MdHeading>(blocks[0]);
        Assert.Equal(1, heading.Level);
        Assert.Equal([new MdText("Done")], heading.Inlines);
        var p = Assert.IsType<MdParagraph>(blocks[1]);
        Assert.Equal([new MdText("All "), new MdText("green", MdStyle.Bold), new MdText(" and "), new MdText("quick", MdStyle.Italic), new MdText(", see "), new MdText("ci.yml", MdStyle.Code), new MdText(".")], p.Inlines);
        Assert.Equal(new MdCode("dotnet test", "powershell"), blocks[2]);
    }

    [Fact]
    public void Lists_keep_their_numbering_and_nesting()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownModel.Parse("3. one\n4. two\n   - inner")));

        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Items.Count);
        Assert.IsType<MdList>(list.Items[1][1]);
    }

    [Fact]
    public void Only_web_links_are_links_and_raw_html_is_shown_as_text()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownModel.Parse("[docs](https://example.com/a) [run](file:///C:/x.exe) <b>hi</b>")));

        Assert.Contains(new MdLink("docs", "https://example.com/a"), p.Inlines);
        Assert.DoesNotContain(p.Inlines, i => i is MdLink { Url: var u } && u.StartsWith("file", StringComparison.Ordinal));
        Assert.Contains("<b>", string.Concat(p.Inlines.OfType<MdText>().Select(t => t.Text)));
    }

    [Fact]
    public void Tables_become_rows_of_cells()
    {
        var table = Assert.IsType<MdTable>(Assert.Single(MarkdownModel.Parse("| a | b |\n|---|---|\n| 1 | 2 |")));

        Assert.True(table.HasHeader);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new MdText("2"), table.Rows[1][1].Single());
    }

    static string CopyOf(string markdown)
    {
        var copied = new List<string>();
        var copy = new CodeCopy(Assert.Single(MarkdownModel.Parse(markdown).OfType<MdCode>()), t => { copied.Add(t); return true; });
        Assert.True(copy.Copy());
        return Assert.Single(copied);
    }

    [Fact]
    public void A_long_command_is_copied_exactly_as_one_line()
    {
        const string command = "pwsh -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\me\\repos\\agent-bridge\\tools\\rehearsal\\run-rehearsal.ps1\" -Profile dev -Machine DEV-VM2 -Checklist docs\\parity-checklist.md -Evidence out\\evidence -Verbose  ";

        Assert.Equal(command, CopyOf($"Run this:\n\n```powershell\n{command}\n```\n"));
    }

    [Fact]
    public void A_multi_line_block_is_copied_with_its_line_breaks_tabs_and_trailing_spaces()
    {
        const string body = "build:\n\tdotnet build -c Release  \n\n    dotnet test";

        Assert.Equal(body, CopyOf($"```make\n{body}\n```"));
    }

    [Fact]
    public void Wrapping_on_screen_does_not_change_the_copied_text()
    {
        var command = "echo " + string.Concat(Enumerable.Repeat("dGhlIGJsb2Igd3JhcHM=", 30));
        var code = Assert.IsType<MdCode>(Assert.Single(MarkdownModel.Parse($"```\n{command}\n\n```")));
        var copy = new CodeCopy(code, _ => true);

        // The block draws one unbroken line, which the view soft-wraps, and copy takes the block itself.
        Assert.Equal(command, copy.Shown);
        Assert.Equal(command + "\n", copy.Text);
    }

    [Fact]
    public void Copy_says_copied_then_copy_again_and_a_refused_clipboard_says_copy_failed()
    {
        var time = new FakeTimeProvider();
        var copy = new CodeCopy(new MdCode("git status", null), _ => true, time: time);

        Assert.Equal("Copy", copy.Label);
        copy.Copy();
        Assert.Equal("Copied", copy.Label);
        time.Advance(CodeCopy.ResetAfter);
        Assert.Equal("Copy", copy.Label);

        var refused = new CodeCopy(new MdCode("git status", null), _ => throw new System.Runtime.InteropServices.COMException("clipboard busy"), time: time);
        Assert.False(refused.Copy());
        Assert.Equal("Copy failed", refused.Label);
    }
}

public sealed class UsageTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    static AgentUsage Agent(string label, double percent, DateTimeOffset? measured = null, string? error = null) => new()
    {
        Agent = AgentKind.Copilot,
        Label = label,
        // An account each: the view keeps one row per account, and these are rows apart.
        Account = label == "GitHub Copilot" ? "octo" : label.ToLowerInvariant(),
        Plan = "pro",
        Source = UsageSource.Api,
        MeasuredAt = measured ?? Now.AddMinutes(-5),
        Error = error,
        Windows = percent < 0 ? [] : [new UsageWindow
        {
            Key = "month",
            Label = "Premium requests",
            Percent = percent,
            Used = percent * 3,
            Limit = 300,
            Unit = "requests",
            StartsAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            ResetsAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        }],
    };

    [Fact]
    public void Rows_are_fullest_first_coloured_at_75_and_90_with_their_pace_and_reset()
    {
        var vm = new UsageViewModel(new FakeTimeProvider(Now));

        vm.Update(new UsageReport([Agent("Low", 20), Agent("High", 92), Agent("Mid", 76)]));

        Assert.Equal(["High", "Mid", "Low"], vm.Rows.Select(r => r.Label));
        Assert.Equal(["crit", "warn", ""], vm.Rows.Select(r => r.Windows[0].Severity));
        Assert.Equal("High · high 92% · Mid · mid 76% · Low · low 20%", vm.Summary);
        var w = vm.Rows[0].Windows[0];
        Assert.Equal(45.2, w.Elapsed);
        Assert.Equal("47% ahead of pace", w.PaceText);
        Assert.Equal("276 / 300 requests", w.AmountText);
        Assert.Equal("resets in 17d", w.ResetText);
        Assert.Equal("high · pro", vm.Rows[0].Detail);
    }

    [Fact]
    public void A_further_copilot_account_is_a_row_of_its_own_named_by_its_login()
    {
        var vm = new UsageViewModel(new FakeTimeProvider(Now));

        vm.Update(new UsageReport([Agent("GitHub Copilot", 40), Agent("GitHub Copilot", 12) with { Account = "dswett_microsoft", Plan = null, Slot = 2 },
            Agent("GitHub Copilot", -1, error: "No Copilot credential was available for monalisa.") with { Account = "monalisa", Plan = null, Slot = 3 }]));

        Assert.Equal(["octo · pro", "dswett_microsoft", "monalisa"], vm.Rows.Select(r => r.Detail));
        Assert.Equal([null, null, "No Copilot credential was available for monalisa."], vm.Rows.Select(r => r.Error));
    }

    [Fact]
    public void Two_copilot_accounts_with_long_logins_are_told_apart_in_full_and_no_line_says_which_machine_read_them()
    {
        // #225: on a phone the login was cut to "d" while "read on <machine>" kept its space, and
        // the summary read "GitHub Copilot 12% · GitHub Copilot 0%".
        const string first = "danswett-with-a-much-longer-login", second = "dswett_microsoft-enterprise-managed";
        var reader = new UsageReader(new string('b', 32), "DSWETT-DEV-VM1-WITH-A-LONG-NAME");
        var vm = new UsageViewModel(new FakeTimeProvider(Now));

        vm.Update(new UsageReport([Agent("GitHub Copilot", 12) with { Account = first, Plan = null, ReadBy = reader },
            Agent("GitHub Copilot", 0) with { Account = second, Plan = null, Slot = 2, ReadBy = reader }]));

        Assert.Equal($"GitHub Copilot · {first} 12% · GitHub Copilot · {second} 0%", vm.Summary);
        Assert.Equal([first, second], vm.Rows.Select(r => r.Account));
        Assert.Equal([first, second], vm.Rows.Select(r => r.Detail));
        Assert.All(vm.Rows, r => Assert.DoesNotContain("read on", r.Label + r.Detail + r.Age, StringComparison.Ordinal));
        // Kept for the age's tooltip and accessible description.
        Assert.All(vm.Rows, r => Assert.Equal("read on DSWETT-DEV-VM1-WITH-A-LONG-NAME", r.ReadOn));
    }

    [Fact]
    public void A_further_account_that_is_also_the_signed_in_one_is_one_row_and_a_newer_failure_lends_it_its_error()
    {
        var vm = new UsageViewModel(new FakeTimeProvider(Now));

        vm.Update(new UsageReport([Agent("GitHub Copilot", 40, Now.AddHours(-2)),
            Agent("GitHub Copilot", -1, error: "gh has no token for this account") with { Account = "OCTO", Plan = null, Slot = 2 }]));

        var row = Assert.Single(vm.Rows);
        Assert.Equal((40d, "octo · pro", "gh has no token for this account"), (row.Headline, row.Detail, row.Error));
    }

    [Fact]
    public void A_failure_beside_a_fresh_reading_is_not_shown_but_one_beside_an_old_reading_is()
    {
        var vm = new UsageViewModel(new FakeTimeProvider(Now));

        vm.Update(new UsageReport([Agent("Fresh", 10, error: "timed out"), Agent("Old", 10, Now.AddHours(-2), "timed out"), Agent("None", -1, error: "not signed in")]));

        Assert.Equal([null, "timed out", "not signed in"], vm.Rows.Select(r => r.Error));
        Assert.True(vm.Rows.Single(r => r.Label == "Old").Stale);
    }

    [Fact]
    public void A_kept_reading_goes_once_it_is_two_weeks_old_and_signing_out_deletes_what_was_kept()
    {
        var file = Path.Combine(Path.GetTempPath(), "abuk-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var clock = new FakeTimeProvider(Now);
            var vm = new UsageViewModel(clock, new UsageCache(file, clock));
            var reading = Agent("Laptop", 30) with { ReadBy = new UsageReader(new string('a', 32), "Laptop") };
            vm.Update(new UsageReport([reading]), account: "1");
            Assert.True(File.Exists(file));

            // The laptop is offline, and nothing has been heard since; the view redraws on its timer.
            vm.Update(UsageReport.Empty, account: "1");
            clock.Advance(TimeSpan.FromDays(13));
            vm.Refresh();
            Assert.Equal("read on Laptop", Assert.Single(vm.Rows).ReadOn);
            clock.Advance(TimeSpan.FromDays(1));
            vm.Refresh();
            Assert.Empty(vm.Rows);

            vm.Update(new UsageReport([reading with { MeasuredAt = clock.GetUtcNow() }]), account: "1");
            vm.Update(UsageReport.Empty, account: "");
            Assert.False(File.Exists(file));
            var restarted = new UsageViewModel(clock, new UsageCache(file, clock));
            restarted.Update(UsageReport.Empty, account: null);
            Assert.Empty(restarted.Rows);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_failure_kept_beside_a_reading_stands_once_the_reading_has_passed_two_weeks_on_redraw_and_after_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), "abuf-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var clock = new FakeTimeProvider(Now);
            var vm = new UsageViewModel(clock, new UsageCache(file, clock));
            var reading = Agent("Laptop", 30, Now - UsageCache.Kept + TimeSpan.FromHours(1)) with { ReadBy = new UsageReader(new string('a', 32), "Laptop") };
            var failed = Agent("Laptop", -1, Now.AddMinutes(-1), "rate limited") with { ReadBy = new UsageReader(new string('b', 32), "Desk") };
            vm.Update(new UsageReport([reading, failed]), account: "1");
            Assert.Equal((30d, "rate limited"), (Assert.Single(vm.Rows).Headline, vm.Rows[0].Error));

            // Two hours on, nothing heard since: the reading has passed its two weeks, the failure has not.
            clock.Advance(TimeSpan.FromHours(2));
            vm.Refresh();
            Assert.Equal((null, "rate limited"), (Assert.Single(vm.Rows).Headline, vm.Rows[0].Error));
            var restarted = new UsageViewModel(clock, new UsageCache(file, clock));
            restarted.Update(UsageReport.Empty, account: "1");
            Assert.Equal("rate limited", Assert.Single(restarted.Rows).Error);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_machine_a_roster_leaves_out_stays_out_once_there_is_no_roster_until_one_lists_it_again()
    {
        var clock = new FakeTimeProvider(Now);
        var vm = new UsageViewModel(clock, new UsageCache(null, clock));
        // Another host has not heard yet that the old laptop was forgotten, and passes its reading on.
        var passedOn = new UsageReport([Agent("Laptop", 30) with { ReadBy = new UsageReader(new string('a', 32), "Old laptop") }]);
        var desk = new string('d', 32);

        vm.Update(passedOn, account: "1", members: new HashSet<string> { desk });
        Assert.Empty(vm.Rows);
        vm.Update(passedOn, account: "1", members: null);
        Assert.Empty(vm.Rows);
        vm.Update(passedOn, account: "1", members: new HashSet<string> { desk, new string('a', 32) });
        Assert.Equal("read on Old laptop", Assert.Single(vm.Rows).ReadOn);
    }

    [Fact]
    public void Every_allowance_reported_is_shown_though_only_thirty_two_are_kept()
    {
        var file = Path.Combine(Path.GetTempPath(), "abuc-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var clock = new FakeTimeProvider(Now);
            var vm = new UsageViewModel(clock, new UsageCache(file, clock));
            var reader = new UsageReader(new string('a', 32), "Laptop");
            vm.Update(new UsageReport([.. Enumerable.Range(0, 33).Select(i => Agent($"Account{i}", 10, Now.AddMinutes(-i)) with { ReadBy = reader })]), account: "1");
            Assert.Equal(33, vm.Rows.Count);

            var restarted = new UsageViewModel(clock, new UsageCache(file, clock));
            restarted.Update(UsageReport.Empty, account: "1");
            Assert.Equal(32, restarted.Rows.Count);
            // The one read longest ago is the one not kept.
            Assert.DoesNotContain(restarted.Rows, r => r.Detail.StartsWith("account32", StringComparison.Ordinal));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_reading_past_two_weeks_does_not_hide_a_current_failure_of_the_same_allowance()
    {
        var clock = new FakeTimeProvider(Now);
        var vm = new UsageViewModel(clock, new UsageCache(null, clock));
        var expired = Agent("Laptop", 30, Now.AddDays(-15)) with { ReadBy = new UsageReader(new string('a', 32), "Laptop") };
        var failed = Agent("Laptop", -1, Now.AddMinutes(-1), "rate limited") with { ReadBy = new UsageReader(new string('b', 32), "Desk") };

        vm.Update(new UsageReport([expired, failed]), account: "1");

        Assert.Equal("rate limited", Assert.Single(vm.Rows).Error);
    }
}

public sealed class WordsTests
{
    [Fact]
    public void Times_read_as_the_phone_reads_them()
    {
        var t0 = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", Words.Ago(t0, t0.AddSeconds(5)));
        Assert.Equal("3m ago", Words.Ago(t0, t0.AddMinutes(3)));
        Assert.Equal("2h 5m", Words.Duration(t0, t0.AddMinutes(125)));
        Assert.Equal("3d", Words.Duration(t0, t0.AddDays(3)));
        Assert.Equal("due", Words.Until(t0, t0.AddSeconds(1)));
    }

    [Fact]
    public void A_rows_age_is_its_largest_whole_unit_as_the_mock_ups_read()
    {
        var t0 = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("now", Words.Age(t0, t0.AddSeconds(59)));
        Assert.Equal("now", Words.Age(t0, t0.AddSeconds(-5)));
        Assert.Equal("32m", Words.Age(t0, t0.AddMinutes(32)));
        Assert.Equal("10h", Words.Age(t0, t0.AddHours(10).AddMinutes(59)));
        Assert.Equal("47h", Words.Age(t0, t0.AddHours(47)));
        Assert.Equal("3d", Words.Age(t0, t0.AddDays(3)));
    }

    [Fact]
    public void A_long_path_is_cut_in_the_middle_keeping_its_start_and_end()
    {
        var path = @"C:\Users\someone\repos\wt\agent-bridge-u6-app\src\AgentBridge.App\LaunchView.xaml.cs";
        var cut = Words.Middle(path, 40);
        Assert.Equal(40, cut.Length);
        Assert.StartsWith(@"C:\Users\someone\", cut);
        Assert.EndsWith(@"LaunchView.xaml.cs", cut);
        Assert.Contains('\u2026', cut);
        Assert.Equal("short", Words.Middle("short", 40));
        Assert.Equal("", Words.Middle(null, 40));
    }

    [Theory]
    // The head's cut would fall between an emoji's two halves.
    [InlineData("C:\\\U0001F4C1\\some-long-folder\\file.txt", 9, "C:\\\u2026.txt")]
    // The tail's.
    [InlineData("C:\\some-long-folder\\notes-\U0001F4C1.md", 9, "C:\\s\u2026.md")]
    // Whole on either side of the cut.
    [InlineData("\U0001F4C1\U0001F4C1-some-long-folder-\U0001F4C1\U0001F4C1", 11, "\U0001F4C1\U0001F4C1-\u2026-\U0001F4C1\U0001F4C1")]
    public void A_path_cut_in_the_middle_never_keeps_half_a_character(string path, int most, string cut)
    {
        var got = Words.Middle(path, most);
        Assert.Equal(cut, got);
        Assert.True(got.Length <= most);
        Assert.DoesNotContain(got.EnumerateRunes(), r => r == System.Text.Rune.ReplacementChar);
        Assert.True(System.Text.Unicode.Utf8.FromUtf16(got, new byte[got.Length * 3], out _, out _, replaceInvalidSequences: false) == System.Buffers.OperationStatus.Done);
    }
}
