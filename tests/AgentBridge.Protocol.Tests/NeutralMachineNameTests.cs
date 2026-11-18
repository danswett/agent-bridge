using System.Text.RegularExpressions;
using Xunit;

public class NeutralMachineNameTests
{
    [Fact]
    public void App_host_core_protocol_and_push_tests_use_neutral_machine_names()
    {
        var root = Path.GetFullPath(Path.Combine(VectorFiles.Folder, "..", "..", ".."));
        string[] projects = ["App", "Host", "Host.Core", "Protocol"];
        var files = projects.SelectMany(project =>
        {
            var folder = Path.Combine(root, "tests", $"AgentBridge.{project}.Tests");
            return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar)
                    .Any(part => part is "bin" or "obj"));
        }).Append(Path.Combine(root, "spikes", "s4-relay", "js", "test-sw.mjs"));
        // Split spellings keep the lint's own source out of its matches; vectors are fixtures.
        var names = new Regex(@"V[M][1-9]|DSWET[T]-|CP[C]-|AR[M]8|dswett_dev_v[m]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var violations = files.SelectMany(file => File.ReadLines(file)
            .Select((line, index) => (line, number: index + 1))
            .Where(item => names.IsMatch(item.line))
            .Select(item => $"{Path.GetRelativePath(root, file)}:{item.number}")).ToArray();

        Assert.True(violations.Length == 0, "Use neutral machine names in tests:\n" + string.Join("\n", violations));
    }
}
