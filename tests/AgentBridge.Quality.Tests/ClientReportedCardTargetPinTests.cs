using Xunit;

namespace AgentBridge.Quality.Tests;

// Owner decision on #4044 (about 08:35 PT, November 23): the client-reported card target is evidence only and is never used for a decision.
// So only the request type, the audit row type and the one answer writer may name it: no gate, grant, policy, routing or projection can read it.
public class ClientReportedCardTargetPinTests
{
    static readonly string[] Allowed =
    [
        "src/AgentBridge.Host/GateAudit.cs",      // the row field and its shape check
        "src/AgentBridge.Host/SessionService.cs", // the one writer: copies the request's value into the answer row
        "src/AgentBridge.Protocol/Sessions.cs",   // AnswerRequest.CardTarget
    ];

    [Fact]
    public void Only_the_request_the_row_and_the_one_writer_name_the_client_reported_card_target()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var named = ChangedFiles.Tracked(root)
            .Where(path => path.StartsWith("src/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => File.Exists(Path.Combine(root, path)) && File.ReadAllText(Path.Combine(root, path)).Contains("CardTarget", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Allowed, named);
    }

    [Fact]
    public void The_one_writer_only_copies_the_value_into_the_row_and_reads_it_nowhere_else()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var lines = File.ReadAllLines(Path.Combine(root, "src/AgentBridge.Host/SessionService.cs"))
            .Where(line => line.Contains("CardTarget", StringComparison.Ordinal)).Select(line => line.Trim()).ToArray();
        Assert.All(lines, line => Assert.True(
            line.Contains("ClientReportedCardTarget = card", StringComparison.Ordinal) || line.Contains("request.CardTarget", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal),
            $"unexpected use: {line}"));
        Assert.InRange(lines.Length, 1, 4);
    }
}