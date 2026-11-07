using System.Net;
using AgentBridge.HomeAssistant;
using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

// The host's own updater, as far as it can be tried without an installed package.
public sealed class HostUpdatesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abhup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Installing_from_Home_Assistant_waits_for_this_hosts_usage_probe_and_refuses_what_it_cannot_confirm()
    {
        TimeSpan? waited = null;
        var stops = 0;
        var updates = new VelopackHostUpdates(root, "default", () => stops++, _ => { },
            ready: ct => HostUpdateGate.ReadyAsync(() => null, (wait, _) => { waited = wait; return Task.FromResult(true); }, ct));
        await updates.ReadyAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(11), waited);

        // Still finishing when the wait is up.
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VelopackHostUpdates(root, "default", () => stops++, _ => { }, ready: ct => HostUpdateGate.ReadyAsync(() => null, (_, _) => Task.FromResult(false), ct)).ReadyAsync(CancellationToken.None));
        Assert.Contains("still finishing after 11 minutes", busy.Message, StringComparison.Ordinal);

        // Another profile's host, whose probes cannot be held from here: refused before this one's are waited for.
        var settled = false;
        var why = await HostUpdateGate.ReadyAsync(() => "another profile's host (pid 4242) runs from this installation", (_, _) => { settled = true; return Task.FromResult(true); }, CancellationToken.None);
        Assert.StartsWith("another profile's host (pid 4242) runs from this installation, whose usage probes cannot be held from here", why, StringComparison.Ordinal);
        Assert.False(settled);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task Installing_from_Home_Assistant_is_refused_when_another_profiles_host_starts_while_this_ones_probe_finishes()
    {
        var started = false;
        var why = await HostUpdateGate.ReadyAsync(
            () => started ? "another profile's host (pid 6161) runs from this installation" : null,
            (_, _) => { started = true; return Task.FromResult(true); },
            CancellationToken.None);

        Assert.StartsWith("another profile's host (pid 6161) runs from this installation", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_only_host_is_given_no_updater_and_so_serves_no_update_buttons()
    {
        var made = 0;
        IHostUpdates Make() { made++; return new VelopackHostUpdates(root, "default", () => { }, _ => { }); }
        Assert.Null(VelopackHostUpdates.For(HostSettings.ReadOnlyReason, windows: true, Make));
        Assert.Null(VelopackHostUpdates.For(null, windows: false, Make));
        Assert.Equal(0, made);
        Assert.NotNull(VelopackHostUpdates.For(null, windows: true, Make));
    }

    [Fact]
    public void The_app_is_restarted_for_the_profile_of_the_host_that_installed_the_update()
    {
        Assert.Equal(["--background", "--profile", "work"], VelopackHostUpdates.RestartArguments("work"));
        Assert.Equal(["--background", "--profile", "default"], VelopackHostUpdates.RestartArguments("default"));
    }

    [Fact]
    public void A_copy_velopack_did_not_install_has_no_installed_version_and_the_feed_decides_the_releases_page()
    {
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { });
        Assert.Null(updates.Installed);
        Assert.Equal("https://github.com/danswett/agent-bridge/releases", updates.ReleasesUrl);
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "feed.txt"), Path.Combine(root, "releases"));
        Assert.Null(updates.ReleasesUrl);
    }

    [Fact]
    public async Task Only_the_release_a_check_here_found_and_downloaded_is_downloaded_or_applied()
    {
        var stopped = false;
        var updates = new VelopackHostUpdates(root, "default", () => stopped = true, _ => { });
        // Same version, but not the one a check here found (another feed's, say): refused.
        var stranger = new UpdateRelease("0.2.0", null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updates.DownloadAsync(stranger, _ => { }, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => updates.ApplyAndRestart(stranger));
        Assert.Throws<InvalidOperationException>(() => updates.ApplyAndRestart(stranger with { Handle = new object() }));
        Assert.False(stopped);
    }

    [Fact]
    public async Task A_check_does_none_of_its_work_on_the_callers_thread()
    {
        // The connector starts a check from its pass: nothing of it may run there. A context that
        // only queues what is posted to it shows whether any of the check ran before returning.
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "feed.txt"), Path.Combine(root, "releases"));
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { });
        var queued = new Queue<(SendOrPostCallback Callback, object? State)>();
        var before = SynchronizationContext.Current;
        Task<UpdateRelease?> check;
        SynchronizationContext.SetSynchronizationContext(new Queueing(queued));
        try { check = updates.CheckAsync(CancellationToken.None); }
        finally { SynchronizationContext.SetSynchronizationContext(before); }
        Assert.False(check.IsCompleted);
        Assert.NotEmpty(queued);
        while (queued.TryDequeue(out var work)) { work.Callback(work.State); }
        // Not installed here: the check itself fails, but only now, off the caller's thread.
        await Assert.ThrowsAnyAsync<Exception>(() => check);
    }

    sealed class Queueing(Queue<(SendOrPostCallback, object?)> queued) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queued.Enqueue((d, state));
    }

    // A release carrying both architectures, as build/publish-prerelease.ps1 publishes it: x64's
    // packages in the feed copies installed before Arm64 builds read (releases.win.json), Arm64's in
    // their own, named as vpk names them (build/app-channels.ps1). Arm64's is the newer here, so an
    // x64 copy that took it would show.
    [Theory]
    [InlineData("win", "0.1.600", "0.1.601")]
    [InlineData("win", "0.1.601", null)]
    [InlineData("win-arm64", "0.1.600", "0.1.602")]
    [InlineData("win-arm64", "0.1.602", null)]
    public async Task An_installed_copy_is_offered_only_its_own_architectures_release_from_a_feed_carrying_both(string channel, string installed, string? offered)
    {
        var feed = Path.Combine(root, "releases");
        Directory.CreateDirectory(feed);
        static string Feed(string version, string file) =>
            $$"""{"Assets":[{"PackageId":"AgentBridgeApp","Version":"{{version}}","Type":"Full","FileName":"{{file}}","SHA1":"0000000000000000000000000000000000000000","SHA256":"0000000000000000000000000000000000000000000000000000000000000000","Size":1}]}""";
        File.WriteAllText(Path.Combine(feed, "releases.win.json"), Feed("0.1.601", "AgentBridgeApp-0.1.601-full.nupkg"));
        File.WriteAllText(Path.Combine(feed, "releases.win-arm64.json"), Feed("0.1.602", "AgentBridgeApp-0.1.602-win-arm64-full.nupkg"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "feed.txt"), feed);
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var copy = new TestVelopackLocator("AgentBridgeApp", installed, packages, null, null, null, channel);
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { }, copy);

        var found = await updates.CheckAsync(CancellationToken.None);

        Assert.Equal(offered, found?.Version);
    }

    [Fact]
    public async Task Checks_on_GitHub_go_through_the_machines_shared_discovery_so_a_limit_holds_every_check_until_it_resets()
    {
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var copy = new TestVelopackLocator("AgentBridgeApp", "0.1.600", packages, null, null, null, "win");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero));
        var github = new FakeGitHub();
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), github, time, random: () => 0);
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { }, copy, discovery);
        github.Answer = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(2)) } };

        var limited = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => updates.CheckAsync(CancellationToken.None));
        Assert.True(limited.Limited);
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => updates.CheckAsync(CancellationToken.None));
        Assert.Equal(1, github.Requests);

        // A release without this channel's feed offers nothing, as Velopack's own source treats it.
        time.Advance(TimeSpan.FromMinutes(2));
        github.Answer = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"name":"Agent Bridge 0.1.601","prerelease":true,"published_at":"2026-11-04T19:00:00Z","assets":[]}]"""),
        };
        Assert.Null(await updates.CheckAsync(CancellationToken.None));
        Assert.Null(await updates.CheckAsync(CancellationToken.None));
        Assert.Equal(2, github.Requests);
        Assert.Equal("https://api.github.com/repos/danswett/agent-bridge/releases?per_page=10&page=1", github.Url);
    }

    // GitHub's own shape, as the API lists releases: the adapter keeps each release's assets, so
    // Velopack finds the channel's feed among them, and leaves pre-releases out when they are off.
    [Theory]
    [InlineData(true, "0.1.602")]
    [InlineData(false, "0.1.601")]
    public async Task A_release_GitHub_lists_is_offered_from_its_channel_feed_and_a_pre_release_only_when_they_are_on(bool prereleases, string offered)
    {
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var copy = new TestVelopackLocator("AgentBridgeApp", "0.1.600", packages, null, null, null, "win");
        static string Download(string version, string file) => $"https://github.com/danswett/agent-bridge/releases/download/v{version}/{file}";
        static string Release(string version, bool pre, string published) => $$"""
            {"name":"Agent Bridge {{version}}","prerelease":{{(pre ? "true" : "false")}},"published_at":"{{published}}","assets":[
              {"name":"releases.win.json","url":"https://api.github.com/repos/danswett/agent-bridge/releases/assets/1","browser_download_url":"{{Download(version, "releases.win.json")}}","content_type":"application/json"},
              {"name":"AgentBridgeApp-{{version}}-full.nupkg","url":"https://api.github.com/repos/danswett/agent-bridge/releases/assets/2","browser_download_url":"{{Download(version, $"AgentBridgeApp-{version}-full.nupkg")}}","content_type":"application/octet-stream"}]}
            """;
        static string Feed(string version) =>
            $$"""{"Assets":[{"PackageId":"AgentBridgeApp","Version":"{{version}}","Type":"Full","FileName":"AgentBridgeApp-{{version}}-full.nupkg","SHA1":"0000000000000000000000000000000000000000","SHA256":"0000000000000000000000000000000000000000000000000000000000000000","Size":1}]}""";
        var github = new FakeGitHub();
        github.Answer = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{Release("0.1.602", true, "2026-11-04T20:00:00Z")},{Release("0.1.601", false, "2026-11-04T19:00:00Z")}]"),
        };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), github, random: () => 0);
        var feeds = new FakeDownloads(new()
        {
            [Download("0.1.602", "releases.win.json")] = Feed("0.1.602"),
            [Download("0.1.601", "releases.win.json")] = Feed("0.1.601"),
        });
        var source = new DiscoveredGithubSource(VelopackHostUpdates.Repository, prereleases, discovery, feeds);

        var found = await new UpdateManager(source, null, copy).CheckForUpdatesAsync();

        Assert.Equal(offered, found?.TargetFullRelease.Version.ToString());
        Assert.Equal(1, github.Requests);
    }

    // A list whose releases have a field of the wrong type is GitHub's answer that couldn't be
    // read, backed off and never kept, rather than a JsonException from the cache at every check.
    [Theory]
    [InlineData("""[{"name":"Agent Bridge 0.1.601","prerelease":{},"assets":[]}]""")]
    [InlineData("""[{"name":"Agent Bridge 0.1.601","prerelease":true,"published_at":"soon","assets":[]}]""")]
    [InlineData("""[{"name":"Agent Bridge 0.1.601","prerelease":true,"assets":{"name":"releases.win.json"}}]""")]
    public async Task A_release_list_with_a_field_the_updater_cannot_read_is_a_failure_backed_off_not_kept(string list)
    {
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var copy = new TestVelopackLocator("AgentBridgeApp", "0.1.600", packages, null, null, null, "win");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero));
        var github = new FakeGitHub { Answer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list) } };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), github, time, random: () => 0);
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { }, copy, discovery);

        var failed = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => updates.CheckAsync(CancellationToken.None));
        Assert.StartsWith("GitHub's answer to the update check could not be read.", failed.Message);
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => updates.CheckAsync(CancellationToken.None));
        Assert.Equal(1, github.Requests);

        time.Advance(ReleaseDiscovery.FirstBackoff);
        github.Answer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        Assert.Null(await updates.CheckAsync(CancellationToken.None));
        Assert.Equal(2, github.Requests);
    }

    sealed class FakeDownloads(Dictionary<string, string> files) : IFileDownloader
    {
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Task.FromResult(System.Text.Encoding.UTF8.GetBytes(files[url]));
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Task.FromResult(files[url]);
        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new InvalidOperationException("nothing is downloaded here");
    }

    sealed class FakeGitHub : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer = () => new(HttpStatusCode.InternalServerError);
        public int Requests;
        public string? Url;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Url = request.RequestUri?.ToString();
            return Task.FromResult(Answer());
        }
    }

    // #927: the release list from the CDN first while the switch is on, and from GitHub whenever
    // the CDN's can't be trusted. Releases name their feeds under this repository's GitHub releases.
    static string Downloaded(string version, string file) => $"{ReleaseDiscovery.DownloadPrefix}v{version}/{file}";

    static string Listed(string version, string? feed = null, string index = "releases.win.json") => $$"""
        {"tag_name":"v{{version}}","name":"Agent Bridge {{version}}","draft":false,"prerelease":true,"published_at":"2026-11-07T18:00:00Z","assets":[
          {"name":"{{index}}","url":"https://api.github.com/repos/danswett/agent-bridge/releases/assets/1","browser_download_url":"{{feed ?? Downloaded(version, index)}}","content_type":"application/json"}]}
        """;

    static string ChannelFeed(string version) =>
        $$"""{"Assets":[{"PackageId":"AgentBridgeApp","Version":"{{version}}","Type":"Full","FileName":"AgentBridgeApp-{{version}}-full.nupkg","SHA1":"0000000000000000000000000000000000000000","SHA256":"0000000000000000000000000000000000000000000000000000000000000000","Size":1}]}""";

    static HttpResponseMessage Listing(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    // The CDN's answer, saying how old its copy is, as it must for it to be used.
    static HttpResponseMessage Relayed(string json, TimeSpan age = default)
    {
        var answer = Listing(json);
        answer.Headers.Age = age;
        return answer;
    }

    const string Foreign = "https://example.com/releases.win.json";

    // What an installed copy is offered through the shared source, its feeds served from here:
    // each version's own, and one elsewhere offering 0.1.666, which no trusted list names.
    async Task<string?> OfferedAsync(ReleaseDiscovery discovery, string installed = "0.1.600")
    {
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var copy = new TestVelopackLocator("AgentBridgeApp", installed, packages, null, null, null, "win");
        var feeds = new FakeDownloads(new()
        {
            [Downloaded("0.1.599", "releases.win.json")] = ChannelFeed("0.1.599"),
            [Downloaded("0.1.601", "releases.win.json")] = ChannelFeed("0.1.601"),
            [Downloaded("0.1.602", "releases.win.json")] = ChannelFeed("0.1.602"),
            [Foreign] = ChannelFeed("0.1.666"),
            ["https://github.com/someone/agent-bridge/releases/download/v0.1.666/releases.win.json"] = ChannelFeed("0.1.666"),
            [Downloaded("0.1.665", "releases.win.json")] = ChannelFeed("0.1.666"),
        });
        var source = new DiscoveredGithubSource(VelopackHostUpdates.Repository, true, discovery, feeds, () => installed);
        return (await new UpdateManager(source, null, copy).CheckForUpdatesAsync())?.TargetFullRelease.Version.ToString();
    }

    sealed class FakeWeb : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Cdn = () => new(HttpStatusCode.ServiceUnavailable);
        public Func<HttpResponseMessage> GitHub = () => new(HttpStatusCode.InternalServerError);
        public int CdnRequests;
        public int GitHubRequests;
        public string? CdnUrl;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "releases.slipgates.com")
            {
                Interlocked.Increment(ref CdnRequests);
                CdnUrl = request.RequestUri.AbsoluteUri;
                return Task.FromResult(Cdn());
            }
            Interlocked.Increment(ref GitHubRequests);
            return Task.FromResult(GitHub());
        }
    }

    [Fact]
    public async Task With_the_switch_on_the_release_list_is_read_from_the_cdn_first_and_github_is_not_asked()
    {
        var web = new FakeWeb { Cdn = () => Relayed($"[{Listed("0.1.602")},{Listed("0.1.601")}]"), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        var said = new List<string>();
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, log: said.Add, cdn: true);

        Assert.Equal("0.1.602", await OfferedAsync(discovery));

        Assert.Equal((1, 0), (web.CdnRequests, web.GitHubRequests));
        Assert.Equal(ReleaseDiscovery.CdnList, web.CdnUrl);
        Assert.Equal(ReleaseDiscovery.Sources.Cdn, discovery.Source);
        Assert.Contains(said, line => line.StartsWith("update check: the release list came from the CDN", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_cdn_that_answers_with_anything_but_a_list_falls_back_to_github(HttpStatusCode status)
    {
        var web = new FakeWeb { Cdn = () => new HttpResponseMessage(status), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        var said = new List<string>();
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, log: said.Add, cdn: true);

        Assert.Equal("0.1.601", await OfferedAsync(discovery));

        Assert.Equal((1, 1), (web.CdnRequests, web.GitHubRequests));
        Assert.Equal(ReleaseDiscovery.Sources.GitHub, discovery.Source);
        Assert.Contains($"update check: the CDN's release list was not used, as the CDN answered {(int)status}; asking GitHub", said);
    }

    [Theory]
    [InlineData(Foreign)]
    [InlineData("https://github.com/someone/agent-bridge/releases/download/v0.1.666/releases.win.json")]
    [InlineData("https://github.com/danswett/agent-bridge/releases/download/v0.1.665/releases.win.json")]
    public async Task A_cdn_list_naming_a_download_outside_this_projects_github_releases_is_refused_and_github_is_used(string feed)
    {
        var web = new FakeWeb { Cdn = () => Relayed($"[{Listed("0.1.666", feed)},{Listed("0.1.601")}]"), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, cdn: true);

        Assert.Equal("0.1.601", await OfferedAsync(discovery));

        Assert.Equal((1, 1), (web.CdnRequests, web.GitHubRequests));
    }

    [Fact]
    public async Task A_cdn_list_older_than_the_version_installed_here_is_refused_and_github_is_used()
    {
        var web = new FakeWeb { Cdn = () => Relayed($"[{Listed("0.1.599")}]"), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, cdn: true);

        Assert.Equal("0.1.601", await OfferedAsync(discovery));

        Assert.Equal((1, 1), (web.CdnRequests, web.GitHubRequests));
    }

    // A failed read, never "nothing newer": an empty list, one cut short, or one with no feed to install from.
    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"tag_name":"v0.1.602","name":"Agent Bridge 0.1.602","draft":false,"prerelease":true,"assets":[]}]""")]
    [InlineData("""[{"tag_name":"v0.1.602","name":"Agent Bridge 0.1.602","draft":false,"prerelease":true,"assets":[""")]
    public async Task An_empty_cut_short_or_uninstallable_list_from_the_cdn_is_a_failed_read_and_github_is_asked(string list)
    {
        var web = new FakeWeb { Cdn = () => Relayed(list), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, cdn: true);

        Assert.Equal("0.1.601", await OfferedAsync(discovery));

        Assert.Equal((1, 1), (web.CdnRequests, web.GitHubRequests));
    }

    [Fact]
    public async Task A_cdn_copy_counts_as_old_as_the_cdn_says_and_does_not_end_githubs_backoff()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero));
        var web = new FakeWeb { GitHub = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(30)) } } };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, time, random: () => 0, cdn: true);
        Assert.True((await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => OfferedAsync(discovery))).Limited);

        time.Advance(TimeSpan.FromMinutes(5));
        web.Cdn = () => Relayed($"[{Listed("0.1.602")}]", TimeSpan.FromMinutes(3));
        Assert.Equal("0.1.602", await OfferedAsync(discovery));
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromMinutes(3), discovery.FetchedAt);

        // The CDN's copy gone stale and the CDN down: GitHub's refusal still holds, unasked.
        time.Advance(ReleaseDiscovery.Interval);
        web.Cdn = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.True((await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => OfferedAsync(discovery))).Limited);
        Assert.Equal((3, 1), (web.CdnRequests, web.GitHubRequests));
    }

    [Fact]
    public async Task With_the_switch_off_the_cdn_is_never_asked()
    {
        var web = new FakeWeb { Cdn = () => Relayed($"[{Listed("0.1.602")}]"), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0);

        Assert.Equal("0.1.601", await OfferedAsync(discovery));

        Assert.Equal((0, 1), (web.CdnRequests, web.GitHubRequests));
    }

    // Review 6711939053: a copy of unknown age could be any age, so it is not used.
    [Fact]
    public async Task A_cdn_list_that_does_not_say_how_old_it_is_is_not_used_and_github_is_asked()
    {
        var web = new FakeWeb { Cdn = () => Listing($"[{Listed("0.1.602")}]"), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        var said = new List<string>();
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, log: said.Add, cdn: true);

        Assert.Equal("0.1.601", await OfferedAsync(discovery));

        Assert.Equal((1, 1), (web.CdnRequests, web.GitHubRequests));
        Assert.Contains("update check: the CDN's release list was not used, as the CDN didn't say how old its copy is; asking GitHub", said);
    }

    // Review 6711939053: judged by what this machine's channel would be offered, not by the newest
    // tag anywhere: a forged newer release carrying only another channel's index, beside a genuine
    // older Windows one, is a list that would take this machine back.
    [Fact]
    public async Task A_cdn_list_whose_newest_release_carries_only_another_channels_feed_is_refused_below_githubs_last_list()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero));
        var web = new FakeWeb { GitHub = () => Listing($"[{Listed("0.1.602")},{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, time, random: () => 0, cdn: true);
        Assert.Equal("0.1.602", await OfferedAsync(discovery));

        time.Advance(ReleaseDiscovery.Interval);
        web.Cdn = () => Relayed($"[{Listed("0.1.999", index: "releases.linux.json")},{Listed("0.1.601")}]");
        Assert.Equal("0.1.602", await OfferedAsync(discovery));

        Assert.Equal((2, 2), (web.CdnRequests, web.GitHubRequests));
    }

    // Review 6711939053: only GitHub's own list sets the floor, so a relay's list naming a release
    // GitHub doesn't have (its feed can't be read, so the check fails) can't make later genuine
    // relay lists look old.
    [Fact]
    public async Task A_forged_newer_release_from_the_cdn_does_not_make_its_later_genuine_lists_look_old()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero));
        var web = new FakeWeb { Cdn = () => Relayed($"[{Listed("0.1.999")},{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, time, random: () => 0, cdn: true);
        await Assert.ThrowsAnyAsync<Exception>(() => OfferedAsync(discovery));

        time.Advance(ReleaseDiscovery.Interval);
        web.Cdn = () => Relayed($"[{Listed("0.1.602")},{Listed("0.1.601")}]");
        Assert.Equal("0.1.602", await OfferedAsync(discovery));

        Assert.Equal((2, 0), (web.CdnRequests, web.GitHubRequests));
    }

    [Fact]
    public async Task A_feed_txt_wins_over_the_cdn_and_github()
    {
        var feed = Directory.CreateDirectory(Path.Combine(root, "releases")).FullName;
        File.WriteAllText(Path.Combine(feed, "releases.win.json"), ChannelFeed("0.1.605"));
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "app")).FullName, "feed.txt"), feed);
        var web = new FakeWeb { Cdn = () => Relayed($"[{Listed("0.1.602")}]"), GitHub = () => Listing($"[{Listed("0.1.601")}]") };
        using var discovery = new ReleaseDiscovery(Path.Combine(root, "app"), web, random: () => 0, cdn: true);
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var copy = new TestVelopackLocator("AgentBridgeApp", "0.1.600", packages, null, null, null, "win");
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { }, copy, discovery);

        Assert.Equal("0.1.605", (await updates.CheckAsync(CancellationToken.None))?.Version);

        Assert.Equal((0, 0), (web.CdnRequests, web.GitHubRequests));
    }

    [Fact]
    public void Pre_releases_follow_the_apps_setting_for_the_profile_read_as_the_app_reads_it()
    {
        var updates = new VelopackHostUpdates(root, "work", () => { }, _ => { });
        Assert.True(updates.Prereleases());
        var file = Path.Combine(root, "app", "work-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"prereleases": false}""");
        Assert.False(updates.Prereleases());
        File.WriteAllText(file, """{"Prereleases": false}""");
        Assert.False(updates.Prereleases());
        File.WriteAllText(file, """{"prereleases": true}""");
        Assert.True(updates.Prereleases());
        File.WriteAllText(file, "not json");
        Assert.True(updates.Prereleases());
        // Another profile's choice is its own.
        File.WriteAllText(Path.Combine(root, "app", "default-settings.json"), """{"prereleases": false}""");
        File.WriteAllText(file, "{}");
        Assert.True(updates.Prereleases());
    }

    [Fact]
    public async Task A_feed_that_is_configured_but_cannot_be_read_fails_the_check_rather_than_falling_back_to_github()
    {
        // A file another process holds without sharing: Windows refuses the read; elsewhere such
        // locks are only advisory.
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(Path.Combine(root, "app"));
        var feed = Path.Combine(root, "app", "feed.txt");
        File.WriteAllText(feed, Path.Combine(root, "releases"));
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { });
        using (new FileStream(feed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => updates.CheckAsync(CancellationToken.None));
            Assert.Null(updates.ReleasesUrl);
        }
    }
}
