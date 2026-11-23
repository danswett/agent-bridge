using System.Globalization;
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

    // --if-changed trusts a marker only while this young: what the digest misses (check runs and suites, Copilot's review) is at most this stale.
    internal static readonly TimeSpan SweepMarkerMaxAge = TimeSpan.FromMinutes(60);

    // The newest marker on main's head that the merge-rule App posted (config.StatusPoster: a forged one by anyone else can't suppress
    // a judgement) and when. Only the newest 100 statuses are read; one pushed past that is not found, which judges.
    static (SweepMarker? Marker, DateTimeOffset? At) ReadSweepMarker(Session session, string head)
    {
        try
        {
            using var statuses = session.Rest($"repos/{session.Slug}/commits/{head}/statuses?per_page=100");
            foreach (var status in statuses.RootElement.EnumerateArray())
            {
                if (status.GetProperty("context").GetString() != SweepContext || !FromTheApp(session, status)) { continue; }
                var at = status.TryGetProperty("created_at", out var created) && DateTimeOffset.TryParse(created.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
                return (ParseMarker(status.GetProperty("description").GetString()), at);
            }
            return (null, null);
        }
        catch (Exception e) when (IsReadFailure(e)) { return (null, null); }
    }

    static bool FromTheApp(Session session, JsonElement status) =>
        status.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object && creator.TryGetProperty("login", out var login)
        && string.Equals((login.GetString() ?? "").Replace("[bot]", "", StringComparison.OrdinalIgnoreCase), session.Config.StatusPoster, StringComparison.OrdinalIgnoreCase);

    // A digest, from REST only, of what can change a verdict about the open pull requests: number, head, draft, labels, last update
    // and the statuses on the head except merge-rule's own verdict and this marker. Null when unreadable or too many.
    static string? ReadHeadsFingerprint(Session session)
    {
        try
        {
            // The waivers are inputs the verdicts read, and flipping one fires no event.
            var rows = new List<string> { $"waivers|codex={session.CodexWaiver}|arm64={session.Arm64Waiver}" };
            for (var page = 1; ; page++)
            {
                using var list = session.Rest($"repos/{session.Slug}/pulls?state=open&base={Uri.EscapeDataString(session.Config.Base)}&per_page=100&page={page}");
                var items = list.RootElement.EnumerateArray().ToList();
                foreach (var pr in items)
                {
                    var head = pr.GetProperty("head").GetProperty("sha").GetString();
                    if (!IsCommitSha(head)) { return null; }
                    var labels = pr.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").Order(StringComparer.Ordinal);
                    var draft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
                    using var statuses = session.Rest($"repos/{session.Slug}/commits/{head}/statuses?per_page=100");
                    var current = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var status in statuses.RootElement.EnumerateArray())
                    {
                        var context = status.GetProperty("context").GetString() ?? "";
                        if (context is MergeRule.StatusContext or SweepContext) { continue; }
                        current.TryAdd(context, $"{status.GetProperty("state").GetString()}/{(status.TryGetProperty("description", out var text) ? text.GetString() : "")}");
                    }
                    rows.Add($"{pr.GetProperty("number").GetInt32()}|{head}|{draft}|{string.Join(",", labels)}|{pr.GetProperty("updated_at").GetString()}|"
                        + string.Join(";", current.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")));
                }
                if (items.Count < 100) { break; }
                if (page >= 10) { return null; }
            }
            return Digest(string.Join("\n", rows.Order(StringComparer.Ordinal)));
        }
        catch (Exception e) when (IsReadFailure(e)) { return null; }
    }

    static void WriteSweepMarker(Session session, string mainHead, SweepMarker marker, SweepMarker? existing, string? targetUrl, TextWriter output)
    {
        if (existing == marker) { return; }
        try
        {
            session.GitHub.PostStatus(session.Slug, mainHead, "success", SweepContext, MarkerDescription(marker), targetUrl);
            output.WriteLine($"Sweep marker posted on {MergeRule.Short(mainHead)}: {MarkerDescription(marker)}.");
        }
        catch (InvalidOperationException e) { output.WriteLine($"::warning::merge-rule: could not post the sweep marker ({e.Message}); the next run judges in full."); }
    }
}
