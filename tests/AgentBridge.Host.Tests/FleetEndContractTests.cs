using System.Reflection;
using AgentBridge.Host;
using Xunit;

// The `fleet end` refusal codes the CLI accepts are the contract agents script against, so the CLI doc
// must list every one (docs/design/fleet-grants-cli.md, Refusal codes), and the grant design doc must
// name the command that exists.
public sealed class FleetEndContractTests
{
    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return root;
    }
    static string Doc(string name) => File.ReadAllText(Path.Combine(Root(), "docs", "design", name));

    [Fact]
    public void Every_refusal_code_fleet_end_accepts_is_in_the_cli_reason_table()
    {
        var reasons = (string[])typeof(FleetEndCommands).GetField("Reasons", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var doc = Doc("fleet-grants-cli.md");
        var table = doc[doc.IndexOf("## Refusal codes", StringComparison.Ordinal)..];
        var missing = reasons.Where(r => !table.Contains($"| `{r}` |", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, "not in the CLI reason table: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_grant_design_names_the_fleet_end_command_and_the_person_required_code()
    {
        var doc = Doc("fleet-grants.md");
        Assert.Contains("fleet end --grant <id> --session <session-id> --request <uuid>", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-bridge-host stop --session <full-id> --grant <id>", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("`personRequired`", doc, StringComparison.Ordinal);
    }
}
