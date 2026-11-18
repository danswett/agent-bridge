using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

public class NeutralMachineNameTests
{
    [Fact]
    public void Home_Assistant_test_source_uses_neutral_machine_names()
    {
        var folder = ProjectFolder();
        // Recorded JSON under Fixtures is allowed; scan C# source, not build output.
        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));
        // Split spellings keep the lint's own source out of its matches.
        var names = new Regex(@"DSWET[T][-_]|AR[M]8",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var violations = files.SelectMany(file => File.ReadLines(file)
            .Select((line, index) => (line, number: index + 1))
            .Where(item => names.IsMatch(item.line))
            .Select(item => $"{Path.GetRelativePath(folder, file)}:{item.number}")).ToArray();

        Assert.True(violations.Length == 0, "Use neutral machine names in tests:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("ha-contract.json", "slugs,machine,statusAttributes,cards,publications.sessionPublish")]
    [InlineData("ha-contract.json", "publications")]
    [InlineData("ha-decisions.json", "")]
    [InlineData("ha-notify.json", "")]
    [InlineData("ha-replies.json", "")]
    [InlineData("ha-stops.json", "")]
    [InlineData("ha-launch.json", "")]
    [InlineData("ha-update.json", "")]
    [InlineData("ha-upkeep.json", "")]
    public void Recorded_fixtures_use_neutral_machine_names(string fixture, string paths)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
        var names = new Regex(@"DSWET[T][-_]|AR[M]8",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var path in paths.Split(','))
        {
            var value = document.RootElement;
            if (path.Length > 0)
            {
                foreach (var part in path.Split('.')) { value = value.GetProperty(part); }
            }
            Assert.False(names.IsMatch(value.GetRawText()), $"Use neutral machine names in {fixture}:{path}");
        }
    }

    static string ProjectFolder([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
