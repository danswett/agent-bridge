using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Updates;

// How the app and the host find the newest release on GitHub (#530). Velopack's GithubSource asks
// api.github.com for the release list at every check, unauthenticated: 60 requests an hour for
// the whole IP address, shared with everything else behind it (a Cloud PC's or an office's NAT).
// Every profile's app checks on start and every half hour, every profile's host for its Home
// Assistant buttons too, and Settings' Check on every click, so the budget ran out and "Check for
// updates" answered "rate limit exceeded" over and over, signed in or not: the app's GitHub
// sign-in is the relay's, never a token for the API, and the updater borrows no credential.
//
// So every check on this machine comes here, and here goes to GitHub at most once per Interval,
// whichever process or profile asks: the answer, its ETag and when GitHub may be asked again live
// in one file beside the app's (release-discovery.json in %LOCALAPPDATA%\AgentBridge\app), and a
// lock file beside it makes the processes take turns, so overlapping checks wait for one request
// rather than each making their own. After the interval the request is conditional: an unchanged
// list is a 304, answered from the file. A refusal (403 or 429) is not asked again until GitHub
// says (Retry-After, or X-RateLimit-Reset once X-RateLimit-Remaining is 0), and any other failure
// is backed off, doubling up to an hour; jitter spreads the machines behind one address so they
// don't all ask the moment a limit resets. Until then a check fails at once with words saying when
// the next try is, never with "up to date": nothing newer than the last answer is known. Only the
// list counts against the limit; each release's releases.<channel>.json comes from its browser
// download URL, which does not.
public sealed class ReleaseDiscovery : IDisposable
{
    public const string FileName = "release-discovery.json";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IntervalJitter = TimeSpan.FromMinutes(2);
    // GitHub asks for at least a minute after a secondary limit that names no time.
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromHours(1);
    // A primary limit resets within the hour; a header naming later than that is not believed,
    // so one bad answer cannot stop updates for good.
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);
    public static readonly TimeSpan BackoffJitter = TimeSpan.FromSeconds(60);
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(45);
    static readonly HttpClient Shared = Client(new SocketsHttpHandler());

    readonly string folder;
    readonly HttpClient http;
    readonly bool ownsClient;
    readonly TimeProvider time;
    readonly Func<double> random;
    readonly SemaphoreSlim turn = new(1, 1);
    // The state as last read or written, for when the folder cannot be: this process's checks
    // stay bounded even then.
    State memory = new();
    // Whether memory holds a write the file didn't take, and which file it was laid over (its
    // Saved stamp when read): kept over that file, but not over one another process has written
    // since. Compared by identity, not by time, as a clock put back makes every new stamp older.
    bool unsaved;
    DateTimeOffset? underneath;

    // How the lock file is opened; a test stands in for a disk that fails.
    internal Func<string, FileStream> OpenLock { get; set; } =
        path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    // folder: the app's data folder, which the app and every profile's host share.
    public ReleaseDiscovery(string folder, HttpMessageHandler? handler = null, TimeProvider? time = null, Func<double>? random = null)
    {
        this.folder = folder;
        (http, ownsClient) = handler is null ? (Shared, false) : (Client(handler), true);
        this.time = time ?? TimeProvider.System;
        this.random = random ?? Random.Shared.NextDouble;
    }

    static HttpClient Client(HttpMessageHandler handler)
    {
        // The handler is the caller's: a test's fake GitHub serves several of these at once.
        var c = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AgentBridge-updater");
        return c;
    }

    // The API's release list for a repository's web address, as Velopack's GithubSource asks for it.
    public static string ReleasesUrl(string repository)
    {
        var path = new Uri(repository.TrimEnd('/')).AbsolutePath;
        return $"https://api.github.com/repos{path}/releases?per_page=10&page=1";
    }

    string StatePath => Path.Combine(folder, FileName);
    string LockPath => Path.Combine(folder, "release-discovery.lock");

    // When GitHub last answered, as of the last list this process was given: a list from the
    // shared file may be older than the check that read it.
    public DateTimeOffset? FetchedAt => memory.FetchedAt;

    // The release list's JSON: GitHub's, or the last one it gave while that is fresh. When there
    // is neither, throws a ReleaseDiscoveryException (or, cancelled, OperationCanceledException),
    // never anything else.
    public Task<string> ReleasesAsync(string url, CancellationToken cancellationToken) =>
        ReleasesAsync(url, body => body, cancellationToken);

    // The release list as read: a list that read throws JsonException for (a field of the wrong
    // type, `[{"prerelease":{}}]`) is an answer that couldn't be read, so it is neither kept nor
    // served from the file, and is backed off like any other failure, never a raw JsonException.
    public async Task<T> ReleasesAsync<T>(string url, Func<string, T> read, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            using var held = await LockAsync(cancellationToken);
            // Read under the lock: another process may have just asked.
            var state = Sane(Read(), time.GetUtcNow());
            var now = time.GetUtcNow();
            T releases = default!;
            var cached = state.Url == url && state.Body is not null && Readable(state.Body, read, out releases);
            // A backoff first: a clock put back can make an older answer's FreshUntil future again,
            // and serving it then would say a check succeeded that GitHub has just refused.
            if (state.NotBefore is { } wait && now < wait) { throw Held(state); }
            if (cached && now < state.FreshUntil) { return releases; }
            var answer = await AskAsync(url, cached ? state.ETag : null, cancellationToken);
            now = time.GetUtcNow();
            var unchanged = answer.Status == HttpStatusCode.NotModified && cached;
            if (unchanged || answer.Status == HttpStatusCode.OK && answer.Body is not null && Readable(answer.Body, read, out releases))
            {
                state = state with
                {
                    Url = url,
                    Body = unchanged ? state.Body : answer.Body,
                    ETag = answer.ETag ?? (unchanged ? state.ETag : null),
                    FetchedAt = now,
                    FreshUntil = now + Interval + IntervalJitter * random(),
                    NotBefore = null,
                    Failures = 0,
                    Problem = null,
                };
                Write(state);
                return releases;
            }
            var failures = state.Failures + 1;
            var backoff = TimeSpan.FromTicks(Math.Min(LongestBackoff.Ticks, FirstBackoff.Ticks << Math.Min(failures - 1, 16)));
            var after = answer.Wait is { } asked ? Clamp(asked) : backoff;
            state = state with
            {
                NotBefore = now + after + BackoffJitter * random(),
                Failures = failures,
                Problem = answer.Problem ?? Problems.Unexpected,
            };
            Write(state);
            throw Held(state);
        }
        finally { turn.Release(); }
    }

    static TimeSpan Clamp(TimeSpan wait) => wait < FirstBackoff ? FirstBackoff : wait > LongestWait ? LongestWait : wait;

    static bool Readable<T>(string body, Func<string, T> read, out T releases)
    {
        try { releases = read(body); return true; }
        catch (JsonException) { releases = default!; return false; }
    }

    // Times further off than anything here sets were written under a clock since put back (a VM
    // restored from a snapshot): believed, they would hold every check for the whole difference.
    static State Sane(State state, DateTimeOffset now) => state with
    {
        FreshUntil = state.FreshUntil > now + Interval + IntervalJitter ? now : state.FreshUntil,
        NotBefore = state.NotBefore > now + LongestWait + BackoffJitter ? now : state.NotBefore,
    };

    sealed record Answer(HttpStatusCode Status, string? Body = null, string? ETag = null, TimeSpan? Wait = null, string? Problem = null);

    async Task<Answer> AskAsync(string url, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (etag is not null) { request.Headers.TryAddWithoutValidation("If-None-Match", etag); }
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var status = response.StatusCode;
            var tag = response.Headers.ETag?.ToString();
            if (status == HttpStatusCode.NotModified) { return new Answer(status, ETag: tag); }
            if (status == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                // Only a list is kept: anything else would be served as the answer for an interval.
                return IsList(body) ? new Answer(status, body, tag) : new Answer(status, Problem: Problems.Unexpected);
            }
            // The body is never kept or shown: GitHub's names the address it limited. For a
            // public repository's releases, unauthenticated, a 403 is a limit, primary or secondary.
            var problem = status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? Problems.Limited
                : (int)status >= 500 ? Problems.Unreachable : Problems.Refused;
            return new Answer(status, Wait: Wait(response), Problem: problem);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new Answer(0, Problem: Problems.Unreachable);
        }
    }

    // A list of releases, each an object whose fields the adapters read (Velopack's GithubRelease)
    // have their types: `[null]` would read as no releases, so "up to date", and `[1]` or
    // `[{"prerelease":{}}]` would be kept as a success that then failed every check for the
    // interval, so each is an answer that couldn't be read. A field left out is the default.
    static bool IsList(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.EnumerateArray().All(IsRelease);
        }
        catch (JsonException) { return false; }
    }

    static bool IsRelease(JsonElement release) =>
        release.ValueKind == JsonValueKind.Object
        && Is(release, "tag_name", JsonValueKind.String) && Is(release, "name", JsonValueKind.String)
        && Is(release, "prerelease", JsonValueKind.True, JsonValueKind.False)
        && (!release.TryGetProperty("published_at", out var published) || published.ValueKind == JsonValueKind.Null
            || published.ValueKind == JsonValueKind.String && published.TryGetDateTimeOffset(out _))
        && Is(release, "assets", JsonValueKind.Array)
        && (!release.TryGetProperty("assets", out var assets) || assets.ValueKind == JsonValueKind.Null
            || assets.EnumerateArray().All(a => a.ValueKind == JsonValueKind.Object
                && Is(a, "url", JsonValueKind.String) && Is(a, "browser_download_url", JsonValueKind.String)
                && Is(a, "name", JsonValueKind.String) && Is(a, "content_type", JsonValueKind.String)));

    // Absent, or of one of these kinds; null only where the adapters' type takes it (not a bool).
    static bool Is(JsonElement o, string name, params JsonValueKind[] kinds) =>
        !o.TryGetProperty(name, out var v) || kinds.Contains(v.ValueKind)
        || v.ValueKind == JsonValueKind.Null && !kinds.Contains(JsonValueKind.True);

    // How long GitHub asked to be left alone: Retry-After (seconds or a date), else the primary
    // limit's reset once none of it remains.
    TimeSpan? Wait(HttpResponseMessage response)
    {
        var now = time.GetUtcNow();
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta) { return delta; }
            if (retry.Date is { } date) { return date - now; }
        }
        if (Header(response, "X-RateLimit-Remaining") == "0"
            && long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
            // FromUnixTimeSeconds throws past year 9999; a header that far off is no answer.
            && reset <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(reset) - now;
        }
        return null;
    }

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    ReleaseDiscoveryException Held(State state)
    {
        var at = state.NotBefore ?? time.GetUtcNow();
        var what = state.Problem switch
        {
            Problems.Limited => "GitHub is limiting how often this network can check for updates.",
            Problems.Unreachable => "GitHub could not be reached to check for updates.",
            Problems.Unexpected => "GitHub's answer to the update check could not be read.",
            _ => "GitHub turned the update check away.",
        };
        return new ReleaseDiscoveryException(what, at, state.Problem == Problems.Limited, state.FetchedAt);
    }

    // The processes take turns through a file none of them shares while it holds it. A folder
    // that cannot be written leaves only this process's turn, which still bounds its own checks.
    async Task<IDisposable?> LockAsync(CancellationToken cancellationToken)
    {
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return OpenLock(LockPath); }
            catch (UnauthorizedAccessException) { return null; }
            // Only another process holding it is worth waiting for. Any other failure (a disk
            // error, a path that can't be made) won't pass by waiting: this process's turn alone.
            catch (IOException e) when (!Contended(e)) { return null; }
            catch (IOException) when (waited.Elapsed < LockWait) { await Task.Delay(100, cancellationToken); }
            catch (IOException)
            {
                throw new ReleaseDiscoveryException("Another update check on this machine is taking a long time.", time.GetUtcNow() + FirstBackoff, false);
            }
        }
    }

    // A sharing or lock violation on Windows; elsewhere the lock .NET takes for FileShare.None
    // fails with EWOULDBLOCK, which it reports as the errno (11 on Linux, 35 on macOS and BSD).
    internal static bool Contended(IOException e) => OperatingSystem.IsWindows()
        ? (e.HResult & 0xFFFF) is 32 or 33
        : e.HResult is 11 or 35;

    // The file, unless this process's last write didn't take and the file is still the one it
    // was laid over: then what this process holds is newer, and the older file must not undo its
    // backoff (a full disk, a file another process holds).
    State Read()
    {
        try
        {
            if (!File.Exists(StatePath)) { return memory; }
            var disk = JsonSerializer.Deserialize(File.ReadAllText(StatePath), ReleaseDiscoveryJson.Default.State) ?? new State();
            if (unsaved && disk.Saved == underneath) { return memory; }
            (unsaved, underneath) = (false, disk.Saved);
            return memory = disk;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return memory; }
    }

    void Write(State state)
    {
        memory = state = state with { Saved = time.GetUtcNow() };
        try
        {
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, ReleaseDiscoveryJson.Default.State));
            File.Move(temp, StatePath, overwrite: true);
            (unsaved, underneath) = (false, state.Saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unsaved = true; }
    }

    public void Dispose()
    {
        if (ownsClient) { http.Dispose(); }
        turn.Dispose();
    }

    internal static class Problems
    {
        public const string Limited = "limited";
        public const string Unreachable = "unreachable";
        public const string Refused = "refused";
        public const string Unexpected = "unexpected";
    }

    internal sealed record State
    {
        public string? Url { get; init; }
        public string? ETag { get; init; }
        public string? Body { get; init; }
        public DateTimeOffset? FetchedAt { get; init; }
        public DateTimeOffset FreshUntil { get; init; }
        public DateTimeOffset? NotBefore { get; init; }
        public int Failures { get; init; }
        public string? Problem { get; init; }
        public DateTimeOffset? Saved { get; init; }
    }
}

[JsonSerializable(typeof(ReleaseDiscovery.State))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ReleaseDiscoveryJson : JsonSerializerContext;

// A check that could not ask GitHub now, and when it next will. It carries its times as instants
// (RetryAt, LastSuccess), and its message says them in the local time of the process that reads
// it, where it is shown (Settings' status), as the product shows times (docs/status.md, 11-04):
// a status, not an alarm.
public sealed class ReleaseDiscoveryException(string what, DateTimeOffset retryAt, bool limited, DateTimeOffset? lastSuccess = null) : Exception(what)
{
    public string What { get; } = what;
    public DateTimeOffset RetryAt { get; } = retryAt;
    public bool Limited { get; } = limited;
    public DateTimeOffset? LastSuccess { get; } = lastSuccess;

    public override string Message => Describe(TimeZoneInfo.Local, DateTimeOffset.UtcNow, CultureInfo.CurrentCulture);

    public string Describe(TimeZoneInfo zone, DateTimeOffset now, IFormatProvider culture)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        string When(DateTimeOffset at)
        {
            var local = TimeZoneInfo.ConvertTime(at, zone);
            return local.ToString(local.Date == today ? "t" : "g", culture);
        }
        var last = LastSuccess is { } fetched ? $" The last successful check was at {When(fetched)}." : "";
        return $"{What} {Product.Name} will check again after {When(RetryAt)}.{last}";
    }
}
