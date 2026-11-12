using AgentBridge.Host.Delivery.Copilot;
using Xunit;

public class CopilotFormScreenTests
{
    static readonly string[] Titles =
    [
        "Which of these three colors would you prefer for the status indicator light today",
        "Would you like this status indicator light to remain enabled while the machine is working on the next task in this session",
        "Please describe anything else you would like us to know before we apply this status indicator preference to the current session",
    ];

    static readonly CopilotForm Form = new(
    [
        new("colour", CopilotFieldKind.Choice, [], [], false, Label: Titles[0]),
        new("enabled", CopilotFieldKind.Boolean, [], [], false, Label: Titles[1]),
        new("notes", CopilotFieldKind.Text, [], [], false, Label: Titles[2]),
    ], "Three preferences.");

    public static TheoryData<int, int, bool> Windows
    {
        get
        {
            var data = new TheoryData<int, int, bool>();
            foreach (var columns in new[] { 80, 120, 200 })
            foreach (var index in new[] { 0, 1, 2 })
            foreach (var marked in new[] { false, true }) { data.Add(columns, index, marked); }
            return data;
        }
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        while (text.Length > width)
        {
            var cut = text.LastIndexOf(' ', width);
            yield return text[..cut];
            text = text[(cut + 1)..];
        }
        yield return text;
    }

    static List<string> Screen(int columns, int field, bool marked)
    {
        var title = Titles[field];
        var bar = Wrap(marked ? $"[{title}]" : title, columns - 8).ToList();
        var middle = bar.Count / 2;
        if (bar.Count == 1 && !marked) { bar[middle] = " " + bar[middle] + " "; }
        bar[middle] = (field > 0 ? "\u2190 " : "") + bar[middle] + (field < 2 ? " \u2192" : "");
        var rule = new string('\u2500', columns - 2);
        var inputRule = " " + new string('\u2500', columns - 4);
        return
        [
            rule, " Copilot needs information.", " Three preferences.", "",
            .. bar.Select(r => " " + r), "",
            .. Wrap(title + (field < 2 ? ":" : ""), columns - 4).Select(r => " " + r),
            .. field == 2 ? new[] { inputRule, " \u276F", inputRule } : new[] { " \u276F Yes", "   No" }, "",
            .. Wrap("enter accept" + (field < 2 ? " \u00B7 tab next" : "") + (field > 0 ? " \u00B7 shift+tab prev" : "")
                + " \u00B7 ctrl+d decline \u00B7 esc cancel", columns - 4).Select(r => " " + r),
            rule,
        ];
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void A_long_title_card_identifies_its_first_middle_and_last_field_at_each_console_width(int columns, int field, bool marked)
    {
        var rows = Screen(columns, field, marked);
        Assert.Null(CopilotFormScreen.Showing(rows, Form, field));
        Assert.All(Enumerable.Range(0, 3).Where(i => i != field), i => Assert.NotNull(CopilotFormScreen.Showing(rows, Form, i)));
        Assert.True(CopilotFormScreen.Untouched(rows, Form, field));
        Assert.DoesNotContain(CopilotFormScreen.Input(rows, Form, field)!, r => r.Contains("preference", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("heading")]
    [InlineData("hints")]
    [InlineData("permission")]
    public void A_wrapped_heading_does_not_authorize_another_field_or_a_permission_prompt(string change)
    {
        var rows = Screen(80, 1, marked: true);
        if (change == "heading") { rows[rows.FindIndex(r => r.EndsWith("session:", StringComparison.Ordinal))] += " changed"; }
        if (change == "hints") { rows = [.. rows.Select(r => r.Replace("shift+tab prev", "shift+tab next", StringComparison.Ordinal))]; }
        if (change == "permission") { rows.AddRange(["Allow this command?", " \u276F 1. Yes", " 2. No"]); }
        Assert.NotNull(CopilotFormScreen.Showing(rows, Form, 1));
    }
}
