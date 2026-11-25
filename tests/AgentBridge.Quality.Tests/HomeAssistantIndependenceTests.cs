using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentBridge.Quality.Tests;

// HA retirement row 5 (approvals): answering and approving a question must not need Home Assistant. The host
// project as a whole still links the HA connector (it hosts it), so the claim is narrower and structural: the
// projects the answer path is built from never reach the HA project, and the source files that implement the
// answer and approve path never name an HA type. This does not prove a live approval; it keeps the path
// independent so that switching the connector off cannot change it.
public class HomeAssistantIndependenceTests
{
    const string HomeAssistant = "AgentBridge.HomeAssistant";

    static readonly string[] AnswerPathProjects = ["AgentBridge.Protocol", "AgentBridge.Host.Delivery", "AgentBridge.Host.Core"];

    static readonly string[] AnswerPathFiles =
    [
        "src/AgentBridge.Protocol/Answers.cs",
        "src/AgentBridge.Host.Delivery/AnswerService.cs",
        "src/AgentBridge.Host/SessionService.cs",
        "src/AgentBridge.Host/RemoteAuthorization.cs",
        "src/AgentBridge.Host/EscapedApprovals.cs",
        "src/AgentBridge.Host/ApproveCommands.cs",
        "src/AgentBridge.Host/FolderTrustAnswers.cs",
        "src/AgentBridge.Host/GateAudit.cs",
        "src/AgentBridge.Host/GateVerdict.cs",
        "src/AgentBridge.Host/PhoneApprovalEvidence.cs",
    ];

    static readonly Regex HaName = new(@"AgentBridge\.HomeAssistant|\bHa(Connector|Payloads|Replies|Commands|Running)\b", RegexOptions.CultureInvariant);

    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);

    static IEnumerable<string> References(string project) =>
        XDocument.Load(Path.Combine(Root, "src", project, project + ".csproj")).Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/')));

    static HashSet<string> Closure(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>([project]);
        while (todo.Count > 0)
        {
            foreach (var reference in References(todo.Pop())) { if (seen.Add(reference)) { todo.Push(reference); } }
        }

        return seen;
    }

    [Theory]
    [InlineData("AgentBridge.Protocol")]
    [InlineData("AgentBridge.Host.Delivery")]
    [InlineData("AgentBridge.Host.Core")]
    public void A_project_the_answer_path_is_built_from_never_reaches_the_home_assistant_project(string project)
    {
        Assert.Contains(project, AnswerPathProjects);
        Assert.DoesNotContain(HomeAssistant, Closure(project));
    }

    [Fact]
    public void The_source_files_that_answer_and_approve_never_name_a_home_assistant_type()
    {
        foreach (var file in AnswerPathFiles)
        {
            var path = Path.Combine(Root, file);
            Assert.True(File.Exists(path), $"{file} moved or was renamed; update the answer-path list");
            Assert.False(HaName.IsMatch(File.ReadAllText(path)), $"{file} names a Home Assistant type");
        }
    }

    [Fact]
    public void The_guard_does_recognize_a_home_assistant_reference_when_there_is_one()
    {
        Assert.Matches(HaName, "using AgentBridge.HomeAssistant;");
        Assert.Matches(HaName, "var c = new HaConnector(settings);");
        Assert.DoesNotMatch(HaName, "IAgentHaBridgeProbe probe; // the agent bridge, not Home Assistant");
    }
}
