using AgentBridge.Protocol;

namespace AgentBridge.App.Tests;

internal static class LargeMessageTranscriptFixture
{
    public static readonly string Prose = string.Concat(Enumerable.Repeat("Large synthetic message with wrapped prose. ", 350));
    public static readonly string Hash = new('a', 64);
    public static readonly string Response = Prose + "\n\n"
        + "| Lane | State | Commit | Evidence | Next |\n| --- | --- | --- | --- | --- |\n"
        + string.Concat(Enumerable.Range(1, 5).Select(i => $"| Lane {i} | Ready | `{Hash}` | Offline fixture | Review |\n"))
        + "\nLatest response ending.";
    public const string Newest = "Newest transcript message.";

    public static IReadOnlyList<HistoryTurn> Turns =>
    [
        Turn("earlier", "Earlier response."),
        Turn("large", Response),
        Turn("newest", Newest),
    ];

    static HistoryTurn Turn(string id, string response) => new()
    {
        Id = id,
        At = DateTimeOffset.UnixEpoch,
        Prompt = new HistoryPrompt { Text = $"Synthetic prompt {id}" },
        Parts = [new HistoryPart { Id = $"response-{id}", Kind = HistoryPartKind.Response, At = DateTimeOffset.UnixEpoch, Text = response }],
    };
}
