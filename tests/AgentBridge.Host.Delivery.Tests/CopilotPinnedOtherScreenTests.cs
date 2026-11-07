using AgentBridge.Host.Delivery.Copilot;
using Xunit;
using View = AgentBridge.Host.Delivery.Copilot.CopilotFormScreen.PinnedOtherView;

public class CopilotPinnedOtherScreenTests
{
    const string Ask = """{"message":"Which colour?","requestedSchema":{"properties":{"colour":{"type":"string","title":"Colour","enum":["Red","Blue"]}}}}""";
    static readonly CopilotForm Form = CopilotPlannerTests.Ask(Ask).Form;
    static readonly string Rule = new('\u2500', 80);

    static List<string> Screen(string text = "Type your answer...") =>
    [
        Rule,
        " Copilot needs information.",
        " Which colour?",
        "",
        " Colour:",
        " Choose a colour or enter your own",
        "   Red",
        "   Blue",
        " \u276F Other (type your answer below)",
        "",
        " \u2191/\u2193 select \u00B7 enter accept \u00B7 ctrl+d decline \u00B7 esc cancel",
        Rule,
        "\u257B" + new string('\u2584', 79),
        "\u2503 " + text,
        "\u2579" + new string('\u2580', 79),
        "",
    ];

    [Fact]
    public void The_captured_placeholder_is_ambiguous_not_proof_of_an_empty_box()
    {
        Assert.Equal(View.PlaceholderOrDraft, CopilotFormScreen.PinnedOther(Screen(), Form));
    }

    [Fact]
    public void The_captured_words_are_recognized_only_as_text_or_a_draft()
    {
        Assert.Equal(View.TextOrDraft, CopilotFormScreen.PinnedOther(Screen("violet at machine"), Form));
    }

    [Fact]
    public void A_blank_row_cannot_prove_the_box_empty()
    {
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(Screen(""), Form));
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(Screen("   "), Form));
    }

    [Theory]
    [InlineData("Type your")]
    [InlineData("Type your\u2026")]
    [InlineData("words\tmore")]
    public void Clipped_or_malformed_text_is_unrecognized(string text)
    {
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(Screen(text), Form));
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(Screen(new string('x', 79)), Form));
    }

    [Fact]
    public void An_above_form_box_or_anything_under_the_box_is_unrecognized()
    {
        var above = Screen();
        var box = above.GetRange(12, 3);
        above.RemoveRange(12, 3);
        above.InsertRange(0, box);
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(above, Form));

        var below = Screen();
        below.Add("  \u276F 1. Yes");
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(below, Form));
    }

    [Fact]
    public void A_wrong_form_label_box_border_or_wrapped_box_is_unrecognized()
    {
        var wrongMessage = Screen();
        wrongMessage[2] = " A different question";
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(wrongMessage, Form));
        var closed = Screen();
        closed[8] = " \u276F Other (type your answer)";
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(closed, Form));
        var wrongBorder = Screen();
        wrongBorder[12] = "\u257B" + new string('\u2584', 78);
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(wrongBorder, Form));
        var wrapped = Screen("first row");
        wrapped.Insert(14, "\u2503 second row");
        Assert.Equal(View.Unrecognized, CopilotFormScreen.PinnedOther(wrapped, Form));
    }

    [Fact]
    public void Recognizing_the_box_does_not_relax_the_form_screen_guard()
    {
        Assert.NotNull(CopilotFormScreen.Showing(Screen(), Form, 0));
    }
}
