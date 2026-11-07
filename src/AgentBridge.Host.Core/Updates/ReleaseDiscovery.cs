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
//
// The CDN (#927): with ReadCdnFirst on, the list is asked of a relay at releases.slipgates.com
// first, which caches GitHub's answer for every machine, and of GitHub only when the relay can't
// give a list this trusts. The relay is untrusted transport: its list is used only if every asset
// it names downloads from this repository's own GitHub releases (DownloadPrefix), in the release
// that names it, it has something to install, and it is no older than the newest release already
// seen here or installed; anything else is GitHub's to answer, as before. Feeds and packages still
// come from GitHub's download URLs and are still checked against their checksums. A list from the
// relay is as old as the relay's copy (its Age header), never "GitHub answered now": FetchedAt
// says so, and GitHub's own backoff stays in force until GitHub itself answers.
public sealed class ReleaseDiscovery : IDisposable
{
    // The switch, off until the relay is live (SRE, #927): flipped by a release, never by a setting.
    public const bool ReadCdnFirst = false;
    public const string Repository = "https://github.com/danswett/agent-bridge";
    // Pinned here, not configurable: feed.txt is the only override, and a test's alone.
    public const string GitHubList = "https://api.github.com/repos/danswett/agent-bridge/releases?per_page=10&page=1";
    public const string CdnList = "https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10&page=1";
    public const string DownloadPrefix = "https://github.com/danswett/agent-bridge/releases/download/";
    const string AssetApiPrefix = "https://api.github.com/repos/danswett/agent-bridge/releases/assets/";
    // A relay copy older than this is not used: GitHub is asked, or its backoff says when it will be.
    public static readonly TimeSpan CdnOldest = TimeSpan.FromHours(1);
    static readonly TimeSpan CdnWait = TimeSpan.FromSeconds(10);
    // GitHub's page of ten releases is tens of KiB.
    const int CdnLargest = 4 * 1024 * 1024;
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
    // The relay's answer is its own or nothing: a redirect elsewhere is not followed.
    static readonly HttpClient SharedCdn = Client(new SocketsHttpHandler { AllowAutoRedirect = false });

    readonly string folder;
    readonly HttpClient http;
    readonly HttpClient cdnHttp;
    readonly bool ownsClient;
    readonly bool cdn;
    readonly Action<string>? log;
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

    // folder: the app's data folder, which the app and every profile's host share. log: where
    // which source answered is said (the app's or the host's log). cdn: ReadCdnFirst, which only
    // a test sets otherwise.
    public ReleaseDiscovery(string folder, HttpMessageHandler? handler = null, TimeProvider? time = null, Func<double>? random = null,
        Action<string>? log = null, bool cdn = ReadCdnFirst)
    {
        this.folder = folder;
        (http, cdnHttp, ownsClient) = handler is null ? (Shared, SharedCdn, false) : (Client(handler), Client(handler), true);
        this.time = time ?? TimeProvider.System;
        this.random = random ?? Random.Shared.NextDouble;
        this.log = log;
        this.cdn = cdn;
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
    // Which answered the list given last: Sources.Cdn or Sources.GitHub (null before any).
    public string? Source => memory.Source;

    // The release list's JSON: GitHub's, or the last one it gave while that is fresh. When there
    // is neither, throws a ReleaseDiscoveryException (or, cancelled, OperationCanceledException),
    // never anything else.
    public Task<string> ReleasesAsync(string url, CancellationToken cancellationToken) =>
        ReleasesAsync(url, body => body, cancellationToken);

    // The release list as read: a list that read throws JsonException for (a field of the wrong
    // type, `[{"prerelease":{}}]`) is an answer that couldn't be read, so it is neither kept nor
    // served from the file, and is backed off like any other failure, never a raw JsonException.
    // installed: the version installed here, which a relay's list may not be older than.
    public async Task<T> ReleasesAsync<T>(string url, Func<string, T> read, CancellationToken cancellationToken, string? installed = null)
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
            // and serving it then would say a check succeeded that GitHub has just refused. A list
            // the relay gave since GitHub refused is not GitHub's answer, and that refusal doesn't
            // touch it.
            var holding = state.NotBefore is { } wait && now < wait;
            if (cached && now < state.FreshUntil && (!holding || state.Source == Sources.Cdn)) { return releases; }
            if (cdn && url == GitHubList)
            {
                var (body, age, refused) = await AskCdnAsync(state.Url == url ? state.Body : null, installed, cancellationToken);
                now = time.GetUtcNow();
                if (body is not null && Readable(body, read, out var relayed))
                {
                    // GitHub's backoff, its failures and its problem stay as they were: only
                    // GitHub answering ends them (Platform on #927, 6711518771).
                    var asOf = now - (age ?? TimeSpan.Zero);
                    state = state with
                    {
                        Url = url,
                        Body = body,
                        ETag = null,
                        FetchedAt = asOf,
                        FreshUntil = now + Interval + IntervalJitter * random(),
                        Source = Sources.Cdn,
                    };
                    Write(state);
                    log?.Invoke(age is { } a
                        ? $"update check: the release list came from the CDN, from its copy of {Span(a)} ago"
                        : "update check: the release list came from the CDN, which didn't say how old its copy is");
                    return relayed;
                }
                log?.Invoke($"update check: the CDN's release list was not used, as {refused ?? "it could not be read"}; {(holding ? "GitHub is not asked again yet" : "asking GitHub")}");
            }
            if (holding) { throw Held(state); }
            var answer = await AskAsync(url, cached && state.Source != Sources.Cdn ? state.ETag : null, cancellationToken);
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
                    Source = Sources.GitHub,
                };
                Write(state);
                if (cdn) { log?.Invoke("update check: the release list came from GitHub"); }
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

    // The relay's list and how old its copy is, or why it is not used: words for the log, never
    // an exception's text or the relay's body.
    async Task<(string? Body, TimeSpan? Age, string? Refused)> AskCdnAsync(string? seen, string? installed, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CdnList);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(CdnWait);
        try
        {
            using var response = await cdnHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            if (response.StatusCode != HttpStatusCode.OK) { return (null, null, $"the CDN answered {(int)response.StatusCode}"); }
            // Whatever the handler did, the answer must be the relay's own for exactly this
            // page: a redirect followed elsewhere is not it.
            if (response.RequestMessage?.RequestUri?.AbsoluteUri is { } answered && answered != CdnList) { return (null, null, "the CDN sent the check elsewhere"); }
            var age = response.Headers.Age;
            if (age > CdnOldest) { return (null, age, $"its copy was {Span(age.Value)} old"); }
            // Read as UTF-8 whatever charset it claims (GitHub's list is JSON), and only so much:
            // a reply this can't decode or that never ends is the relay's failure, not the check's.
            if (response.Content.Headers.ContentLength > CdnLargest) { return (null, age, "its list was too large"); }
            await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, bounded.Token)) > 0)
            {
                if (bytes.Length + read > CdnLargest) { return (null, age, "its list was too large"); }
                bytes.Write(buffer, 0, read);
            }
            var body = System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            return CdnProblem(body, seen, installed) is { } problem ? (null, age, problem) : (body, age, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, null, "the CDN could not be reached");
        }
    }

    // Why a list from the relay is not used, or null. It must be a list GitHub could have given
    // (IsList), every release in it published (not a draft) and every asset downloading from this
    // repository's own releases, in the release that names it: a checksum only proves a package
    // matches the feed that names it, so a list naming another host's feed could name a hostile
    // package (SRE, #927). Empty, cut short, or with nothing to install is a failed read, never
    // "nothing newer". And it may not go backwards: older than a list seen here, or than what is
    // installed, it would hold this machine back.
    internal static string? CdnProblem(string body, string? seen, string? installed)
    {
        if (!IsList(body)) { return "its list could not be read"; }
        using var doc = JsonDocument.Parse(body);
        Version? newest = null;
        var installable = false;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False) { return "its list named an unpublished release"; }
            if (!release.TryGetProperty("tag_name", out var tagValue) || tagValue.GetString() is not { } tag || TagVersion(tag) is not { } version)
            {
                return "its list named a release without a version";
            }
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) { continue; }
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var download = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                var api = asset.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (name is null || download != DownloadPrefix + tag + "/" + name || !Canonical(download, DownloadPrefix + tag + "/")
                    || api is not null && !Canonical(api, AssetApiPrefix))
                {
                    return "its list named a download outside this project's GitHub releases";
                }
                if (name.StartsWith("releases.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                {
                    installable = true;
                    newest = newest is null || version > newest ? version : newest;
                }
            }
        }
        if (!installable || newest is null) { return "its list had nothing to install"; }
        if (Newest(seen) is { } before && newest < before) { return "its list was older than one already seen here"; }
        if (installed is not null && Version.TryParse(installed, out var current) && newest < current) { return "its list was older than the version installed here"; }
        return null;
    }

    // Exactly the prefix and one more plain path segment: no query, fragment, escape, credentials
    // or dot segments that could lead the download anywhere else.
    static bool Canonical(string url, string prefix)
    {
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
        var rest = url[prefix.Length..];
        return rest.Length > 0 && rest is not "." and not ".."
            && rest.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsoluteUri == url;
    }

    static Version? TagVersion(string tag) =>
        tag.Length > 1 && tag[0] == 'v' && tag.Skip(1).All(c => char.IsAsciiDigit(c) || c == '.')
        && Version.TryParse(tag[1..], out var version) ? version : null;

    // The newest version a list kept here names, whichever source gave it.
    static Version? Newest(string? body)
    {
        if (body is null) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { return null; }
            Version? newest = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind == JsonValueKind.Object && release.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                    && TagVersion(t.GetString()!) is { } v && (newest is null || v > newest)) { newest = v; }
            }
            return newest;
        }
        catch (JsonException) { return null; }
    }

    static string Span(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? "under a minute"
        : span < TimeSpan.FromHours(2) ? $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")}"
        : $"{(int)span.TotalHours} hours";

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
        if (ownsClient) { http.Dispose(); cdnHttp.Dispose(); }
        turn.Dispose();
    }

    // Which answered a list: said in the log and kept with the list (Source).
    public static class Sources
    {
        public const string Cdn = "cdn";
        public const string GitHub = "github";
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
        public string? Source { get; init; }
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
