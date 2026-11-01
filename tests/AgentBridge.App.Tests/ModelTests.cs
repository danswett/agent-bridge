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
        Assert.Equal("High 92% · Mid 76% · Low 20%", vm.Summary);
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
}
