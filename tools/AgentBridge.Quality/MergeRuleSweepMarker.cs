using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

/// <summary>
/// The sweep marker: a commit status on main's head with two digests of what the last FULL judgement saw, main's state and
/// the open pull requests. A scoped run under a red main widens only without it; --if-changed judges nothing with it unchanged.
/// An absent, unreadable or malformed marker is never trusted: the run judges.
/// </summary>
static partial class MergeRuleCommand
{
    internal const string SweepContext = "merge-rule-sweep";

    internal sealed record SweepMarker(string Main, string Heads);

    static readonly Regex MarkerText = new(@"^main=(?<main>[0-9a-f]{16}) heads=(?<heads>[0-9a-f]{16})$", RegexOptions.CultureInvariant);

    static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];

    internal static string MainPart(MainFact main) => Digest($"{main.State}|{main.Evidence ?? main.Reason}");

    internal static string MarkerDescription(SweepMarker marker) => $"main={marker.Main} heads={marker.Heads}";

    internal static SweepMarker? ParseMarker(string? description) =>
        description is not null && MarkerText.Match(description) is { Success: true } m ? new(m.Groups["main"].Value, m.Groups["heads"].Value) : null;

    // Main's head and the newest marker on it (none on an older head: a new commit is a new state); null when unreadable.
    static (string? MainHead, SweepMarker? Marker) ReadSweepMarker(Session session)
    {
        try
        {
            using var reference = session.Rest($"repos/{session.Slug}/git/ref/heads/{Uri.EscapeDataString(session.Config.Base)}");
            var head = reference.RootElement.GetProperty("object").GetProperty("sha").GetString();
            if (!IsCommitSha(head))
                return (null, null);
            using var statuses = session.Rest($"repos/{session.Slug}/commits/{head}/statuses?per_page=100");
            // GitHub lists a commit's statuses newest first: the first marker is the latest.
            foreach (var status in statuses.RootElement.EnumerateArray())
                if (status.GetProperty("context").GetString() == SweepContext)
                    return (head, ParseMarker(status.GetProperty("description").GetString()));
            return (head, null);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return (null, null);
        }
    }

    // A digest, from REST only, of what can change a verdict about the open pull requests: number, head, draft, labels, last update
    // and the statuses on the head except merge-rule's own verdict and this marker. Null when unreadable or too many.
    static string? ReadHeadsFingerprint(Session session)
    {
        try
        {
            var rows = new List<string>();
            for (var page = 1; ; page++)
            {
                using var list = session.Rest($"repos/{session.Slug}/pulls?state=open&base={Uri.EscapeDataString(session.Config.Base)}&per_page=100&page={page}");
                var items = list.RootElement.EnumerateArray().ToList();
                foreach (var pr in items)
                {
                    var head = pr.GetProperty("head").GetProperty("sha").GetString();
                    if (!IsCommitSha(head))
                        return null;
                    var labels = pr.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").Order(StringComparer.Ordinal);
                    var draft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
                    using var statuses = session.Rest($"repos/{session.Slug}/commits/{head}/statuses?per_page=100");
                    // Newest first, so the first of each context is its current state.
                    var current = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var status in statuses.RootElement.EnumerateArray())
                    {
                        var context = status.GetProperty("context").GetString() ?? "";
                        if (context is MergeRule.StatusContext or SweepContext)
                            continue;
                        current.TryAdd(context, $"{status.GetProperty("state").GetString()}/{(status.TryGetProperty("description", out var text) ? text.GetString() : "")}");
                    }
                    rows.Add($"{pr.GetProperty("number").GetInt32()}|{head}|{draft}|{string.Join(",", labels)}|{pr.GetProperty("updated_at").GetString()}|"
                        + string.Join(";", current.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")));
                }
                if (items.Count < 100)
                    break;
                if (page >= 10)
                    return null;
            }
            return Digest(string.Join("\n", rows.Order(StringComparer.Ordinal)));
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }
    }

    // A marker that cannot be posted only costs the next run one more full judgement.
    static void WriteSweepMarker(Session session, string mainHead, SweepMarker marker, SweepMarker? existing, string? targetUrl, TextWriter output)
    {
        if (existing == marker)
            return;
        try
        {
            session.GitHub.PostStatus(session.Slug, mainHead, "success", SweepContext, MarkerDescription(marker), targetUrl);
            output.WriteLine($"Sweep marker posted on {MergeRule.Short(mainHead)}: {MarkerDescription(marker)}.");
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine($"::warning::merge-rule: could not post the sweep marker ({e.Message}); the next run judges in full.");
        }
    }
}
