using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using S2.Noise;

namespace S4;

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

// Spike storage: a key file in the profile folder. The product keeps the private key in
// the TPM or DPAPI (design §5.7, spike S6).
public sealed class Identity
{
    public string Folder { get; }
    public P256KeyPair Key { get; }
    public SignInResult? Account { get; private set; }

    Identity(string folder, P256KeyPair key, SignInResult? account) { Folder = folder; Key = key; Account = account; }

    public static Identity Load(string profile)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridgeS4", profile);
        Directory.CreateDirectory(folder);
        var keyFile = Path.Combine(folder, "key.json");
        P256KeyPair key;
        if (File.Exists(keyFile))
        {
            var k = JsonDocument.Parse(File.ReadAllText(keyFile)).RootElement;
            key = P256KeyPair.FromParameters(Wire.Unhex(k.GetProperty("d").GetString()!), Wire.Unhex(k.GetProperty("x").GetString()!), Wire.Unhex(k.GetProperty("y").GetString()!));
        }
        else
        {
            key = P256KeyPair.Generate();
            var (d, x, y) = key.Export();
            File.WriteAllText(keyFile, JsonSerializer.Serialize(new { d = Wire.Hex(d), x = Wire.Hex(x), y = Wire.Hex(y) }));
        }
        var accountFile = Path.Combine(folder, "account.json");
        var account = File.Exists(accountFile) ? JsonSerializer.Deserialize<SignInResult>(File.ReadAllText(accountFile)) : null;
        return new Identity(folder, key, account);
    }

    public void Save(SignInResult account)
    {
        Account = account;
        File.WriteAllText(Path.Combine(Folder, "account.json"), JsonSerializer.Serialize(account));
    }
}

// What a host answers in the spike, standing in for Bridge Protocol v1's snapshot.
public static class DemoService
{
    public static Task<object?> HandleAsync(string machine, string op, JsonElement data) => Task.FromResult<object?>(op switch
    {
        "ping" => new { at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        "echo" => data,
        "snapshot" => new
        {
            machine,
            os = Environment.OSVersion.ToString(),
            at = DateTimeOffset.UtcNow,
            sessions = new[]
            {
                new { id = "c0ffee01", agent = "copilot", status = "waiting", title = "Which database should I use?" },
                new { id = "c0ffee02", agent = "claude", status = "working", title = "Refactor the relay client" },
            },
        },
        _ => null,
    });
}
