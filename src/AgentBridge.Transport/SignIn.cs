using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentBridge.Transport;

public sealed record SignInResult(string Token, string AccountId, string Login);

public static class SignIn
{
    // GitHub's device flow, through the relay: the relay holds the OAuth app's client id,
    // exchanges the GitHub token for its own account token, and keeps neither secret nor
    // GitHub token. show(userCode, verificationUri) tells the person what to enter where.
    public static async Task<SignInResult> DeviceFlowAsync(HttpClient http, Uri relay, Action<string, string> show, CancellationToken ct = default)
    {
        var start = await http.PostAsync(new Uri(relay, "/v1/auth/device"), null, ct);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<JsonElement>(ct);
        show(s.GetProperty("userCode").GetString()!, s.GetProperty("verificationUri").GetString()!);
        var deviceCode = s.GetProperty("deviceCode").GetString()!;
        var interval = s.GetProperty("interval").GetInt32();
        var deadline = DateTime.UtcNow.AddSeconds(s.GetProperty("expiresIn").GetInt32());
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var r = await http.PostAsJsonAsync(new Uri(relay, "/v1/auth/token"), new { deviceCode }, ct);
            var j = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (r.StatusCode == HttpStatusCode.Accepted)
            {
                if (j.TryGetProperty("interval", out var i)) { interval = i.GetInt32(); }
                continue;
            }
            if (!r.IsSuccessStatusCode) { throw new InvalidOperationException($"sign-in failed: {j.GetProperty("error").GetString()}"); }
            return new SignInResult(j.GetProperty("token").GetString()!, j.GetProperty("accountId").GetString()!, j.GetProperty("login").GetString()!);
        }
        throw new TimeoutException("sign-in code expired");
    }
}
