using System.Xml.Linq;
using Xunit;

namespace AgentBridge.App.Tests;

// A live region set from code says nothing until its change is raised: WinUI does not raise
// LiveRegionChanged when a TextBlock's text changes, so a screen reader never heard the escaped
// approval's "Approve once is available" (#836 review). Each file that makes one raises it.
public class LiveRegionTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return root;
    }

    [Fact]
    public void Every_live_region_the_app_makes_in_code_is_announced_by_raising_its_change()
    {
        var silent = Directory.EnumerateFiles(Path.Combine(Root, "src", "AgentBridge.App"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var code && code.Contains("SetLiveSetting(", StringComparison.Ordinal) && !code.Contains("AutomationEvents.LiveRegionChanged", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Root, f));

        Assert.Empty(silent);
    }

    public static IEnumerable<object[]> XamlLiveRegions()
    {
        return Directory.EnumerateFiles(Path.Combine(Root, "src", "AgentBridge.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => XDocument.Load(f).Descendants().Any(IsLiveRegion))
            .Select(f => new object[] { Path.GetRelativePath(Root, f) });
    }

    static bool IsLiveRegion(XElement element) =>
        element.Attribute("AutomationProperties.LiveSetting") is { Value: not "Off" };

    [Theory]
    [MemberData(nameof(XamlLiveRegions))]
    public void Every_live_region_the_app_declares_in_XAML_has_a_change_announcement(string xaml)
    {
        var behind = Path.Combine(Root, xaml + ".cs");

        Assert.True(File.Exists(behind), $"{xaml} needs a code-behind that announces its live regions.");
        var code = File.ReadAllText(behind);
        foreach (var region in XDocument.Load(Path.Combine(Root, xaml)).Descendants().Where(IsLiveRegion))
        {
            // Receipt lines delegate their announcements to the attached property's callback.
            var announcer = region.Attribute(XName.Get("ReceiptMarkView.Announcement", "using:AgentBridge.App")) is not null
                ? File.ReadAllText(Path.Combine(Path.GetDirectoryName(behind)!, "ReceiptMarkView.cs"))
                : code;
            Assert.Contains("AutomationEvents.LiveRegionChanged", announcer, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("DelegateInspector", "DelegateNote")]
    [InlineData("SessionDetails", "EndStatus")]
    public void Bound_note_and_end_status_announce_after_their_text_changes(string view, string region)
    {
        var xaml = Path.Combine(Root, "src", "AgentBridge.App", view + ".xaml");
        var line = Assert.Single(XDocument.Load(xaml).Descendants(),
            e => (string?)e.Attribute("AutomationProperties.AutomationId") == region);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var code = File.ReadAllText(xaml + ".cs");

        Assert.Equal(region, (string?)line.Attribute(x + "Name"));
        Assert.Contains($"{region}.RegisterPropertyChangedCallback(TextBlock.TextProperty,", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(", code, StringComparison.Ordinal);
        Assert.Contains($"CreatePeerForElement({region})?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", code, StringComparison.Ordinal);
    }
}
