using System.Net;
using System.Security.Cryptography;
using AgentBridge.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The relay's feedback upload (Feedback.cs, docs/protocol/feedback.md) against the in-process
// local backend: a device can create one sealed bundle and nothing else, within limits.
public class FeedbackStoreTests
{
    const long Mib = 1024 * 1024;

    static Task<TestRelay> StartAsync(Action<RelayOptions>? more = null) => TestRelay.StartAsync(o =>
    {
        o.Feedback.Backend = "local";
        more?.Invoke(o);
    });

    static async Task<string> RefusedAsync(Func<Task> request) => (await Assert.ThrowsAsync<BlobRefusedException>(request)).Reason;

    static async Task<HttpStatusCode> PutAsync(TestRelay relay, string url, byte[] body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BlobHttp.Resolve(relay.Url, url)) { Content = new ByteArrayContent(body) };
        using var r = await relay.Http.SendAsync(req);
        return r.StatusCode;
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_bundle_is_created_once_at_a_URL_that_cannot_read_it_back()
    {
        await using var relay = await StartAsync();
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var bundle = RandomNumberGenerator.GetBytes(4096);

        var slot = await phone.Relay.FeedbackPutAsync(4096);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(relay, slot.Url, bundle));
        Assert.Equal(HttpStatusCode.Conflict, await PutAsync(relay, slot.Url, bundle));
        var stored = Assert.Single(((LocalFeedbackSink)relay.App.Services.GetRequiredService<FeedbackService>().Sink!).Stored);
        Assert.Equal(bundle, stored.Value);
        // Not the account, the device or anything readable in the name; and no way to read it.
        Assert.Equal(22, stored.Key.Length);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await relay.Http.GetAsync(BlobHttp.Resolve(relay.Url, slot.Url))).StatusCode);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Only_exactly_the_ticketed_size_is_taken()
    {
        await using var relay = await StartAsync();
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var slot = await phone.Relay.FeedbackPutAsync(4096);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await PutAsync(relay, slot.Url, new byte[4097]));
        Assert.Equal(HttpStatusCode.BadRequest, await PutAsync(relay, slot.Url, new byte[100]));
        Assert.Empty(((LocalFeedbackSink)relay.App.Services.GetRequiredService<FeedbackService>().Sink!).Stored);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(relay, slot.Url, new byte[4096]));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_URL_lasts_minutes_and_a_URL_from_another_name_does_not_work()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await StartAsync(o => o.Feedback.Time = time);
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var slot = await phone.Relay.FeedbackPutAsync(4096);
        Assert.InRange(slot.Expires - time.GetUtcNow(), TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));

        var parts = slot.Url.Split('.');
        var forged = string.Join('.', parts[0], "8192", parts[2], parts[3]);
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync(relay, forged, new byte[8192]));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync(relay, slot.Url, new byte[4096]));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Sizes_are_capped_and_must_be_a_padded_bucket()
    {
        await using var relay = await StartAsync(o => o.Feedback.MaxBytes = 2 * Mib);
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");

        Assert.Equal(2 * Mib, phone.Relay.FeedbackMaxBytes);
        Assert.Equal("too-large", await RefusedAsync(() => phone.Relay.FeedbackPutAsync(3 * Mib)));
        Assert.Equal("bad-size", await RefusedAsync(() => phone.Relay.FeedbackPutAsync(5000)));
        Assert.Equal("bad-request", await RefusedAsync(() => phone.Relay.FeedbackPutAsync(0)));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_account_is_limited_per_minute_and_per_day_and_other_accounts_are_unaffected()
    {
        await using var relay = await StartAsync(o =>
        {
            o.Feedback.MaxPutsPerMinute = 2;
            o.Feedback.MaxPutsPerAccountPerDay = 3;
        });
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var other = await relay.ConnectAsync(relay.TokenFor(2, "hubot"), "other", "pwa");

        await phone.Relay.FeedbackPutAsync(4096);
        await phone.Relay.FeedbackPutAsync(4096);
        Assert.Equal("rate-limited", await RefusedAsync(() => phone.Relay.FeedbackPutAsync(4096)));
        await other.Relay.FeedbackPutAsync(4096);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_accounts_bytes_per_day_are_limited()
    {
        await using var relay = await StartAsync(o => o.Feedback.MaxBytesPerAccountPerDay = 2 * 4096);
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");

        await phone.Relay.FeedbackPutAsync(4096);
        await phone.Relay.FeedbackPutAsync(4096);
        Assert.Equal("quota", await RefusedAsync(() => phone.Relay.FeedbackPutAsync(4096)));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_relay_without_a_feedback_store_says_so_at_admission_and_refuses_uploads()
    {
        await using var relay = await TestRelay.StartAsync();
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");

        Assert.Equal(0, phone.Relay.FeedbackMaxBytes);
        Assert.Equal("disabled", await RefusedAsync(() => phone.Relay.FeedbackPutAsync(4096)));
    }
}
