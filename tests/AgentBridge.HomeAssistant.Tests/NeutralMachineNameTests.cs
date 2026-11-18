using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

public class NeutralMachineNameTests
{
    [Theory]
    [InlineData("ha-contract.json", "slugs,machine,statusAttributes,cards,publications.sessionPublish")]
    [InlineData("ha-contract.json", "publications")]
    [InlineData("ha-decisions.json", "")]
    [InlineData("ha-notify.json", "")]
    [InlineData("ha-replies.json", "")]
    [InlineData("ha-stops.json", "")]
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
}
