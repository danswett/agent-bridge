using System.Text.RegularExpressions;
using Xunit;

// HA retirement row 19 ("agent started this session" marker and attribution of writes to the agent), HA off (#1396). The behaviour is covered
// elsewhere: the driver marker and principal (Host.Core DriverTests), the launch audit rows naming the agent's principal and request
// (AgentLaunchGateTests, AgentAuditLogTests), and caller checks (AgentLaunchCallerCheckTests). This pins the dependency: none of the sources that
// decide who started a session or write the audit line refers to Home Assistant, so the replacement for the Home Assistant agent user is
// independent of it. It reads sources at run time; it does not exercise behaviour. (The launch environment that hands a launched agent a Home
// Assistant URL lives in LaunchService and is a separate concern, outside this scan.)
public sealed class HaOffAttributionTests
{
    static string Source
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.Host"))) { return Path.Combine(dir.FullName, "src"); }
            }
            throw new DirectoryNotFoundException("The repository src folder was not found above " + AppContext.BaseDirectory);
        }
    }

    static readonly Regex HomeAssistant = new(@"HomeAssistant|\bHa[A-Z]\w*", RegexOptions.CultureInvariant);

    static IEnumerable<string> Attribution()
    {
        foreach (var file in new[] { "AgentAuditLog.cs", "AgentAuditRotation.cs", "AgentLaunchCallerCheck.cs", "AgentLaunchGate.cs", "AgentPipeLaunch.cs" })
        {
            yield return Path.Combine(Source, "AgentBridge.Host", file);
        }
        yield return Path.Combine(Source, "AgentBridge.Host.Delivery", "Launch", "LaunchProvenance.cs");
        // Host.Core holds the driver marker (who drove a turn); its project file only lets the Home Assistant assemblies see its internals.
        foreach (var file in Directory.GetFiles(Path.Combine(Source, "AgentBridge.Host.Core"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            yield return file;
        }
    }

    [Fact]
    public void The_sources_that_decide_who_started_a_session_and_write_the_audit_line_do_not_refer_to_Home_Assistant()
    {
        var files = Attribution().ToList();
        Assert.True(files.Count > 6, "the attribution sources were not found");
        Assert.All(files, f => Assert.True(File.Exists(f), f + " is missing"));
        var hits = files.Where(f => HomeAssistant.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(hits);
    }
}
