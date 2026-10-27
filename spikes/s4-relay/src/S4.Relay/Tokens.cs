using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S4.Relay;

public sealed class RelayOptions
{
    public string GitHubClientId { get; set; } = "";
    // Optional. With it the relay revokes each GitHub token as soon as it has read the
    // account id, so no GitHub credential outlives sign-in anywhere.
    public string? GitHubClientSecret { get; set; }
    public string GitHubBaseUrl { get; set; } = "https://github.com";
    public string GitHubApiUrl { get; set; } = "https://api.github.com";
    // Base64, 32 bytes. Without it tokens are signed with a per-process key and every
    // restart signs everyone out.
    public string? TokenKey { get; set; }
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public int MaxFramesPerSecond { get; set; } = 200;
    public int MaxDevicesPerAccount { get; set; } = 32;

    // Test hooks. Tap observes every forwarded payload; Route lets a test play a hostile
    // relay that redirects or forges routing.
    public Action<string, string, byte[]>? Tap { get; set; }
    public Func<string, string, (string From, string To)>? Route { get; set; }
}

public sealed record Account(string Id, string Login);

// Relay account tokens: HMAC-signed, stateless. The relay never stores GitHub tokens.
public sealed class Tokens
{
    readonly byte[] key;
    readonly TimeSpan lifetime;

    public Tokens(RelayOptions o, ILogger<Tokens> log)
    {
        lifetime = o.TokenLifetime;
        if (string.IsNullOrEmpty(o.TokenKey))
        {
            log.LogWarning("Relay:TokenKey is not set; tokens will not survive a restart");
            key = RandomNumberGenerator.GetBytes(32);
        }
        else { key = Convert.FromBase64String(o.TokenKey); }
    }

    public (string Token, DateTimeOffset Expires) Issue(Account a)
    {
        var expires = DateTimeOffset.UtcNow + lifetime;
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new { a = a.Id, l = a.Login, e = expires.ToUnixTimeSeconds() }));
        return ($"{body}.{B64(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)))}", expires);
    }

    public Account? Validate(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 2 }) { return null; }
        try
        {
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Unb64(parts[1]))) { return null; }
            var j = JsonDocument.Parse(Unb64(parts[0])).RootElement;
            if (DateTimeOffset.FromUnixTimeSeconds(j.GetProperty("e").GetInt64()) < DateTimeOffset.UtcNow) { return null; }
            return new Account(j.GetProperty("a").GetString()!, j.GetProperty("l").GetString()!);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException) { return null; }
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] Unb64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}
