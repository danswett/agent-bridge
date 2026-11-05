using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AgentBridge.HomeAssistant;

// How the HA connector's own release checks have gone since this host started (#819). These are
// the connector's checks only, not the app's (AutoUpdater) or a cached discovery's: a machine that
// reads "current" on its update entity may never have reached the feed, and Home Assistant's
// last_changed is no evidence of a check (an unchanged publish is skipped; a retained one can be
// old). Kept in memory, so each start begins at NotYetAnswered.
public enum HaUpdateCheckOutcome
{
    // This connector does not check: checks are off, or the host has no updater to act for it.
    NotServed,
    // Checks are on but none has answered since this host started.
    NotYetAnswered,
    // The last check reached the feed and found nothing newer than the version installed: a valid
    // answer on a correct feed, never an error.
    Current,
    // The last check reached the feed and found a newer release.
    Newer,
    // The last check could not establish the latest release; FailureClass says why.
    Failed,
}

// Why NotServed: the switch (homeAssistant checkForUpdates) or the updater (none for a read-only
// host, or a copy the updater did not install).
public enum HaUpdateCheckNotServed { None, ChecksOff, NoUpdater }

// A safe, closed view: no exception text, path, URL or account ever lands here, only a class from
// HaUpdateCheckFailure and an HTTP status number.
public sealed record HaUpdateCheckHealth(
    HaUpdateCheckOutcome Outcome,
    HaUpdateCheckNotServed NotServed,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? FailureClass,
    string Installed,
    string? Latest);

// The line the update entity's notes (release_summary, an existing field) end with while they
// show no newer release: when this host's Home Assistant connector last began a release check and
// when one last succeeded (#819). This connector's own checks only, not the machine's update health
// as a whole nor the app's. Times, never the failure's text (see HaUpdateCheckFailure).
public static class HaUpdateCheckLabel
{
    public const string Lead = "This host's Home Assistant update check:";

    public static string Labelled(string notes, bool answered, DateTimeOffset? attemptAt, DateTimeOffset? successAt)
    {
        // Before any answer the latest release is unknown, whatever latest_version shows meanwhile
        // (the version installed, kept so the entity is not unavailable at every start): never
        // "current", and never a success.
        var line = answered || successAt is not null
            ? $"{Lead} last begun {Stamp(attemptAt)}; last succeeded {(successAt is { } s ? Stamp(s) : "not since this host started")}."
            : $"{Lead} latest release unknown, not answered yet since this host started{(attemptAt is { } a ? $" (begun {Stamp(a)})" : "")}.";
        return notes.Length == 0 ? line : $"{notes}\n\n{line}";
    }

    static string Stamp(DateTimeOffset? at) =>
        at is { } t ? t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
}

// The class of a release check that failed, and the words for it on the entity and in the log.
// The exception's own message is never shown: it can carry a feed URL with a token in it, a local
// path (feed.txt, the package folder), or whatever text a proxy put in its answer, and the update
// entity's notes travel through MQTT to every dashboard (#819).
public static class HaUpdateCheckFailure
{
    public const string TimedOut = "timed-out";
    public const string RateLimited = "rate-limited";
    public const string Refused = "refused";
    public const string HttpError = "http-error";
    public const string Unreachable = "unreachable";
    public const string Unreadable = "unreadable";
    // Any failure of no class above: a fixed word, never its type's name (a type can be named by
    // whoever wrote it, dynamic or user-defined), its namespace, or its text.
    public const string CheckFailed = "check-failed";
    public const string CheckFailedWords = "the check failed";

    // A null failure is a check that ended cancelled without the connector stopping: a request
    // that timed out (HttpClient's timeout is a cancellation).
    public static (string Class, string Words) Classify(Exception? failure)
    {
        if (failure is null) { return (TimedOut, "the request timed out"); }
        foreach (var e in Chain(failure))
        {
            switch (e)
            {
                case TimeoutException or OperationCanceledException:
                    return (TimedOut, "the request timed out");
                case HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }:
                    return (RateLimited, "rate limited");
                // GitHub answers 403 to an unauthenticated caller over its rate limit, but also to
                // one it refuses for other reasons: which it was is not known here.
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden }:
                    return (Refused, "the release feed refused the request (HTTP 403)");
                case HttpRequestException { StatusCode: { } status }:
                    return (HttpError, $"the release feed answered HTTP {((int)status).ToString(CultureInfo.InvariantCulture)}");
                case HttpRequestException or System.Net.Sockets.SocketException:
                    return (Unreachable, "the release feed could not be reached");
                case JsonException or FormatException or InvalidDataException:
                    return (Unreadable, "the release feed's answer could not be read");
                default:
                    break;
            }
        }
        return (CheckFailed, CheckFailedWords);
    }

    // The failure and what it wraps, aggregates opened, a few levels deep.
    static IEnumerable<Exception> Chain(Exception failure)
    {
        var pending = new Queue<(Exception E, int Depth)>();
        pending.Enqueue((failure, 0));
        while (pending.Count > 0)
        {
            var (e, depth) = pending.Dequeue();
            yield return e;
            if (depth >= 4) { continue; }
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) { pending.Enqueue((inner, depth + 1)); }
            }
            else if (e.InnerException is { } inner) { pending.Enqueue((inner, depth + 1)); }
        }
    }
}
