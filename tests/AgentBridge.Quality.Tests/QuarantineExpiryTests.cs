using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

public class QuarantineExpiryTests
{
    static void Check(string text, DateTimeOffset now)
    {
        foreach (Match match in Regex.Matches(text, "quarantine-" + """expires=([^;\s"\\]+)"""))
        {
            var expiry = DateTimeOffset.ParseExact(match.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            Assert.True(expiry > now, $"Expired quarantine ({expiry:O}): restore the test by reverting its quarantine PR.");
        }
    }

    [Fact]
    public void Quarantines_in_test_sources_have_not_expired()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var files = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "pwa", "test"), "*.spec.mjs", SearchOption.AllDirectories));
        foreach (var file in files)
            Check(File.ReadAllText(file), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Expiry_checks_are_selected_even_for_unrelated_product_changes()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var selector = new Selector(Catalog.Load(Path.Combine(root, "quality", "catalog.yml")), ProjectGraph.Load(root));
        Assert.Contains("quality-tool", Fixture.Ids(selector.Select(["src/AgentBridge.Hooks/HookParser.cs"], ring: 0)));
    }

    [Fact]
    public void Expiry_at_the_deadline_fails_and_a_future_deadline_passes()
    {
        var reason = "quarantine-" + "expires=2026-11-18T08:00:00Z; issue #42";
        var deadline = DateTimeOffset.Parse("2026-11-18T08:00:00Z", CultureInfo.InvariantCulture);
        Check(reason, deadline.AddSeconds(-1));
        Assert.ThrowsAny<Exception>(() => Check(reason, deadline));
        Assert.ThrowsAny<Exception>(() => Check(reason, deadline.AddSeconds(1)));
        Assert.Throws<FormatException>(() => Check("quarantine-" + "expires=invalid; issue #42", deadline));
    }
}
