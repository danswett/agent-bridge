using AgentBridge.App.Core;
using Xunit;

public sealed class UpdateFeedPolicyTests
{
    [Theory]
    [InlineData("https://updates.example/releases", false)]
    [InlineData("https://updates.example/releases", true)]
    [InlineData("http://localhost:8080/feed", true)]
    [InlineData("http://127.0.0.1:8080/feed", true)]
    [InlineData("http://[::1]:8080/feed", true)]
    public void Https_or_explicit_development_loopback_feeds_are_web_sources(string feed, bool development)
    {
        Assert.Equal(new Uri(feed), UpdateFeedPolicy.WebUri(feed, development));
    }

    [Theory]
    [InlineData("http://updates.example/feed", false)]
    [InlineData("http://updates.example/feed", true)]
    [InlineData("http://localhost:8080/feed", false)]
    [InlineData("http://127.0.0.1/feed", false)]
    [InlineData("http://[::1]/feed", false)]
    [InlineData("http://localhost.evil.example/feed", true)]
    [InlineData("http://127.0.0.1.evil.example/feed", true)]
    [InlineData("http://0.0.0.0/feed", true)]
    [InlineData("ftp://updates.example/feed", true)]
    [InlineData("http://", true)]
    public void Insecure_or_unrecognized_remote_feeds_are_rejected_before_a_source_is_created(string feed, bool development)
    {
        Assert.Throws<InvalidOperationException>(() => UpdateFeedPolicy.WebUri(feed, development));
    }

    [Theory]
    [InlineData("")]
    [InlineData("updates")]
    [InlineData(@"C:\test-feed")]
    [InlineData("/tmp/test-feed")]
    public void Local_directory_overrides_remain_available_without_development_mode(string feed)
    {
        Assert.Null(UpdateFeedPolicy.WebUri(feed));
    }
    [Theory]
    [InlineData(@"\\server\test-feed")]
    [InlineData(@"\\server@SSL\DavWWWRoot\feed")]
    [InlineData(@"\\?\UNC\server\test-feed")]
    [InlineData(@"\\.\UNC\server\test-feed")]
    [InlineData("//server/test-feed")]
    [InlineData(@"/\server\test-feed")]
    [InlineData(@"  \\server\test-feed  ")]
    [InlineData("file://server/test-feed")]
    [InlineData("file://localhost/test-feed")]
    [InlineData("file:////server/test-feed")]
    [InlineData("file:///%5c%5cserver/share")]
    [InlineData("file:////%73erver/share")]
    [InlineData(@"\\?\UNC\server@80\DavWWWRoot\feed")]
    [InlineData("file:///\\server/test-feed")]
    public void Network_file_authorities_and_unc_paths_never_enter_local_file_mode(string feed)
    {
        foreach (var development in new[] { false, true })
        {
            Assert.Throws<InvalidOperationException>(() => UpdateFeedPolicy.WebUri(feed, development));
        }
    }

    [Fact]
    public void A_local_file_uri_is_converted_to_its_directory_path()
    {
        Assert.Null(UpdateFeedPolicy.WebUri("file:///tmp/test-feed"));
        Assert.Equal("/tmp/test-feed", UpdateFeedPolicy.LocalDirectory("file:///tmp/test-feed"));
    }

}
