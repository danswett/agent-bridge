using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

/// <summary>
/// On main's logs every failing job restored an exact-key archives cache that failed verification against the signed indexes: the index had moved since the
/// cache was written, and an exact key is immutable, so it failed on every run. The key carries the index hash (download.sh --cache-key), and so does the restore
/// prefix, so a moved index is a miss and a prefix never restores an entry written under another index. (S2, #4104)
/// </summary>
public class PlaywrightPackagesCacheKeyTests
{
    static YamlMappingNode Action()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "actions", "playwright-system-packages", "action.yml"))));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    static string Scalar(YamlNode node, string key) => ((YamlScalarNode)((YamlMappingNode)node)[key]).Value!;

    [Fact]
    public void The_archives_cache_key_carries_the_signed_indexes_hash_in_its_key_and_its_restore_prefix()
    {
        var debs = ((YamlSequenceNode)((YamlMappingNode)Action()["runs"])["steps"]).Children.OfType<YamlMappingNode>()
            .Single(s => s.Children.ContainsKey("id") && Scalar(s, "id") == "debs");
        var with = (YamlMappingNode)debs["with"];
        const string Packages = "${{ steps.identity.outputs.packages-hash }}", Index = "${{ steps.identity.outputs.index-hash }}", Ref = "${{ steps.identity.outputs.ref-hash }}";

        Assert.Contains($"{Packages}-{Index}-{Ref}", Scalar(with, "key"), StringComparison.Ordinal);
        var prefixes = Scalar(with, "restore-keys").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(prefixes);
        Assert.All(prefixes, p => Assert.EndsWith($"{Packages}-{Index}-", p, StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Scalar(with, "key"), System.Text.RegularExpressions.Regex.Escape(Index)));
    }
}