using System.Text.RegularExpressions;
using Xunit;

// Quality on #4019: the Home Assistant connector reaches the answer, stop and launch services ONLY through the three composed,
// audited delegates (HaRunning.AnswerDelegate, StopDelegate, LaunchDelegate), so a press is always an audited HA-path action the
// windowed gate can count. A direct call anywhere under the HA files would be an unaudited path: this scan turns red on it.
public sealed class HaAuditedPathGuardTests
{
    static readonly (string What, Regex Pattern)[] Forbidden =
    [
        ("a plain AnswerAsync", new(@"\.AnswerAsync\(")),
        ("a stop that is not StopAudit.StopAsync", new(@"(?<!StopAudit)\.StopAsync\(")),
        ("a launch that is not GateLaunchAudit.LaunchAsync", new(@"(?<!GateLaunchAudit)\.LaunchAsync\(")),
        ("another launch entry", new(@"\b(?:LaunchGateAsync|LaunchFleetAsync|LaunchAuditedAsync)\b")),
        ("a SessionService start", new(@"\bSessionService\s*\.\s*Start|\bnew\s+SessionService\b")),
    ];

    // What HaRunning must call, so Start cannot bypass the composed delegates.
    static readonly string[] Required = ["AnswerDelegate(answers, launchAudit)", "StopDelegate(stops, source, launchAudit)", "LaunchDelegate(launcher, launchAudit)"];

    static string Strip(string text) => Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\r\n]*", "");

    // The problems in one source file. The connector's own sender is named in exactly one place, HaAnswerAudit.cs.
    internal static List<string> Check(string path, string text)
    {
        var code = Strip(text);
        var problems = Forbidden.Where(f => f.Pattern.IsMatch(code)).Select(f => $"{path}: {f.What}").ToList();
        if (!path.EndsWith("HaAnswerAudit.cs", StringComparison.Ordinal) && Regex.IsMatch(code, @"\bsender:")) { problems.Add($"{path}: a direct sender"); }
        if (path.EndsWith("HaRunning.cs", StringComparison.Ordinal)) { problems.AddRange(Required.Where(r => !code.Contains(r)).Select(r => $"{path}: no longer calls {r}")); }
        return problems;
    }

    static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.Host", "HaRunning.cs"))) { return Path.Combine(dir.FullName, "src"); }
        }
        throw new DirectoryNotFoundException("the source tree is not beside the tests");
    }

    static IEnumerable<string> HaFiles(string src) =>
        Directory.GetFiles(Path.Combine(src, "AgentBridge.HomeAssistant"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Concat([Path.Combine(src, "AgentBridge.Host", "HaRunning.cs"), Path.Combine(src, "AgentBridge.Host", "HaAnswerAudit.cs")]);

    [Fact]
    public void The_HA_files_reach_the_answer_stop_and_launch_services_only_through_the_composed_audited_delegates()
    {
        var src = SourceRoot();
        var problems = HaFiles(src).SelectMany(f => Check(Path.GetRelativePath(src, f), File.ReadAllText(f))).ToList();
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("answers.AnswerAsync(request, ct, sender: \"home-assistant\");")]
    [InlineData("return stops.StopAsync(request, ct);")]
    [InlineData("return launcher.LaunchAsync(request, \"home-assistant\", ct);")]
    [InlineData("return launcher.LaunchGateAsync(request, peer, prepare, ct);")]
    [InlineData("var s = new SessionService(source, machine);")]
    [InlineData("SessionService.StartSomething();")]
    public void A_planted_direct_call_in_HaRunning_turns_the_guard_red(string planted)
    {
        var real = File.ReadAllText(Path.Combine(SourceRoot(), "AgentBridge.Host", "HaRunning.cs"));
        Assert.Empty(Check("AgentBridge.Host/HaRunning.cs", real));
        Assert.NotEmpty(Check("AgentBridge.Host/HaRunning.cs", real + "\n" + planted + "\n"));
    }

    [Theory]
    [InlineData("var r = await answers.AnswerAsync(request, ct);")]
    [InlineData("await stopper.StopAsync(request, ct);")]
    [InlineData("var r = await launcher.LaunchAsync(request, \"x\", ct);")]
    [InlineData("answers.AnswerAuditedAsync(request, prepare, ct, sender: \"home-assistant\");")]
    public void A_planted_direct_call_in_the_HA_connector_project_turns_the_guard_red(string planted) =>
        Assert.NotEmpty(Check("AgentBridge.HomeAssistant/HaConnector.cs", "class C { void M() { " + planted + " } }"));

    [Fact]
    public void Dropping_a_composed_delegate_from_Start_turns_the_guard_red()
    {
        var real = File.ReadAllText(Path.Combine(SourceRoot(), "AgentBridge.Host", "HaRunning.cs"));
        Assert.Contains("no longer calls LaunchDelegate(launcher, launchAudit)", string.Join('\n', Check("AgentBridge.Host/HaRunning.cs", real.Replace("LaunchDelegate(launcher, launchAudit)", "null"))));
    }
}
