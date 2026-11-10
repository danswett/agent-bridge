using System.Xml.Linq;
using AgentBridge.App.Core;
using Xunit;

// The question card's place in the Windows conversation (#1657 S2): where it was asked, after the
// reply it follows, and a bar above the reply box when it has scrolled out of view.
public sealed class QuestionPlaceTests
{
    static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Read from the source tree (found by walking up from the test's folder), so the project file is untouched.
    static XElement SessionView()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.App", "SessionView.xaml"))) { dir = dir.Parent; }
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "AgentBridge.App", "SessionView.xaml")).Root!;
    }

    static string? NameOf(XElement e) => (string?)e.Attribute(Xaml + "Name");

    [Fact]
    public void The_question_card_is_in_the_conversation_footer_after_the_reply_not_in_the_header_before_it()
    {
        var list = SessionView().Descendants().Single(e => NameOf(e) == "ConversationList");
        var header = list.Elements().Single(e => e.Name.LocalName == "ListView.Header");
        var footer = list.Elements().Single(e => e.Name.LocalName == "ListView.Footer");
        Assert.DoesNotContain(header.Descendants(), e => NameOf(e) == "QuestionCard");
        Assert.Contains(footer.Descendants(), e => NameOf(e) == "QuestionCard");
    }

    [Fact]
    public void The_question_waiting_bar_sits_in_its_own_row_above_the_reply_box()
    {
        var root = SessionView();
        var bar = root.Descendants().Single(e => NameOf(e) == "QuestionWaiting");
        var composer = root.Descendants().Single(e => NameOf(e) == "Composer");
        Assert.Equal(int.Parse((string)composer.Attribute("Grid.Row")!) - 1, int.Parse((string)bar.Attribute("Grid.Row")!));
    }

    [Theory]
    [InlineData(true, true, -500, 200, 600, true)]   // scrolled past it
    [InlineData(true, true, 700, 200, 600, true)]    // not reached yet
    [InlineData(true, true, 100, 200, 600, false)]   // in view
    [InlineData(true, true, 500, 200, 600, false)]   // partly in view
    [InlineData(false, true, -500, 200, 600, false)] // answered or closed
    [InlineData(true, false, -500, 200, 600, false)] // another tab
    public void The_bar_shows_only_for_an_open_question_wholly_out_of_view(bool asked, bool viewing, double top, double height, double viewport, bool shows)
        => Assert.Equal(shows, QuestionBar.Shows(asked, viewing, top, height, viewport));
}
