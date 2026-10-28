using System.Net;
using System.Text.Json;

namespace AgentBridge.Host.Core.Usage;

public sealed record UsageHttpResponse(int Status, string Body);

// One vendor GET. Separate so tests replace the network whole and still exercise the
// parsing, which is where the vendors differ and where the bugs are.
public interface IUsageHttp
{
    Task<UsageHttpResponse> GetAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken);
}

public sealed class UsageHttp(TimeSpan timeout) : IUsageHttp, IDisposable
{
    readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = timeout };

    public async Task<UsageHttpResponse> GetAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in headers) { request.Headers.TryAddWithoutValidation(name, value); }
        using var response = await client.SendAsync(request, cancellationToken);
        return new UsageHttpResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public void Dispose() => client.Dispose();
}

// A vendor's answer that is not a success: the status alone, never the body, which can echo
// what was sent.
public sealed class UsageHttpException(int status) : Exception($"the server answered {status} ({(HttpStatusCode)status})")
{
    public int Status { get; } = status;
}

internal static class UsageFetch
{
    // One read, retried past a transient failure: a few attempts close together, to ride out
    // a reset handshake between two polls rather than hammer a vendor that is down. A refusal
    // is an answer and is not retried (401 is still 401 in 400 ms); 408 and 429 are about
    // timing, and 5xx and network failures may pass.
    public static async Task<JsonDocument> GetJsonAsync(IUsageHttp http, Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers,
        UsageOptions options, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, options.RequestAttempts);
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var response = await http.GetAsync(uri, headers, cancellationToken);
                if (response.Status is >= 200 and < 300) { return JsonDocument.Parse(response.Body); }
                var refusal = new UsageHttpException(response.Status);
                if (response.Status is >= 400 and < 500 and not 408 and not 429) { throw refusal; }
                last = refusal;
            }
            catch (UsageHttpException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException) { last = e; }
            if (attempt < attempts && options.RetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.RetryDelay * attempt, cancellationToken);
            }
        }
        throw last!;
    }

    // The whole exception chain, not its head: .NET's outer message for a failed handshake
    // ("The SSL connection could not be established, see inner exception.") names no cause,
    // and the cause is a level or two down.
    public static string Describe(Exception e)
    {
        if (e is TaskCanceledException) { return "the request timed out"; }
        var parts = new List<string>();
        for (var x = e; x is not null; x = x.InnerException)
        {
            var message = System.Text.RegularExpressions.Regex.Replace(x.Message.Trim(), @",?\s*see inner exception\.?$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (message.Length > 0 && !parts.Contains(message)) { parts.Add(message); }
        }
        return parts.Count == 0 ? "the reason was not reported" : string.Join(" - ", parts);
    }
}
