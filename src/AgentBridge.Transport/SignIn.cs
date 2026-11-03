using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentBridge.Transport;

public sealed record SignInResult(string Token, string AccountId, string Login);

public static class SignIn
{
    public const string DevicePage = "https://github.com/login/device";
    public static bool IsDevicePage(string? uri) => uri == DevicePage;

    static string RequiredCode(JsonElement value, string name) =>
        value.TryGetProperty(name, out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString())
            ? code.GetString()! : throw new InvalidOperationException("The relay returned an invalid sign-in code.");

    static int Seconds(JsonElement value, string name, int max) =>
        value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int seconds) && seconds >= 1 && seconds <= max
            ? seconds : throw new InvalidOperationException("The relay returned an invalid sign-in interval or lifetime.");

    // GitHub's device flow, through the relay: the relay holds the OAuth app's client id,
    // exchanges the GitHub token for its own account token, and keeps neither secret nor
    // GitHub token. show(userCode, verificationUri) tells the person what to enter where.
    public static async Task<SignInResult> DeviceFlowAsync(HttpClient http, Uri relay, Action<string, string> show, CancellationToken ct = default)
    {
        using var start = await http.PostAsync(new Uri(relay, "/v1/auth/device"), null, ct);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!IsDevicePage(s.GetProperty("verificationUri").GetString())) { throw new InvalidOperationException("The relay returned an untrusted sign-in page."); }
        var deviceCode = RequiredCode(s, "deviceCode");
        var userCode = RequiredCode(s, "userCode");
        var interval = Seconds(s, "interval", 60);
        var deadline = DateTime.UtcNow.AddSeconds(Seconds(s, "expiresIn", 3600));
        show(userCode, DevicePage);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { break; }
            await Task.Delay(remaining.TotalSeconds < interval ? remaining : TimeSpan.FromSeconds(interval), ct);
            if (DateTime.UtcNow >= deadline) { break; }
            using var r = await http.PostAsJsonAsync(new Uri(relay, "/v1/auth/token"), new { deviceCode }, ct);
            var j = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (DateTime.UtcNow >= deadline) { break; }
            if (r.StatusCode == HttpStatusCode.Accepted)
            {
                if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("pending", out var pending) || pending.ValueKind != JsonValueKind.True)
                {
                    throw new InvalidOperationException("The relay returned an invalid polling response.");
                }
                if (j.TryGetProperty("interval", out _)) { interval = Math.Max(interval, Seconds(j, "interval", 60)); }
                continue;
            }
            if (!r.IsSuccessStatusCode) { throw new InvalidOperationException($"sign-in failed: {j.GetProperty("error").GetString()}"); }
            return new SignInResult(j.GetProperty("token").GetString()!, j.GetProperty("accountId").GetString()!, j.GetProperty("login").GetString()!);
        }
        throw new TimeoutException("sign-in code expired");
    }
}
