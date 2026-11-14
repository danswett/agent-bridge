using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Protocol;

// Validated native requests, not a wire DTO that can bypass Read.
public sealed class ApnsPushRequest
{
    public const string Capability = "push.apns";
    public const string Subscribe = "push.apns.subscribe";
    public const string Unsubscribe = "push.apns.unsubscribe";
    public const string List = "push.apns.list";
    public string Operation { get; }
    public string? Target { get; }
    public string? Token { get; }
    public string? KeyId { get; }
    public PushKeys? Keys { get; }
    public string? Device { get; }

    ApnsPushRequest(string operation, string? target = null, string? token = null,
        string? keyId = null, PushKeys? keys = null, string? device = null)
    {
        Operation = operation;
        Target = target;
        Token = token;
        KeyId = keyId;
        Keys = keys;
        Device = device;
    }

    public static ApnsPushRequest Read(string operation, JsonElement data)
    {
        if (operation == List) { Shape(data); return new(operation); }
        if (operation == Unsubscribe) { Shape(data, "target"); return new(operation, ReadTarget(data)); }
        if (operation != Subscribe) { throw Bad(); }
        Shape(data, "token", "target", "keyId", "keys", "device");
        var target = ReadTarget(data);
        var token = Text(data, "token", 1024);
        if (token.Length % 2 != 0 || !token.All(char.IsAsciiHexDigit)) { throw Bad(); }
        _ = Bytes(data, "keyId", 32);
        if (!data.TryGetProperty("keys", out var keys)) { throw Bad(); }
        Shape(keys, "p256dh", "auth");
        var point = Bytes(keys, "p256dh", 65);
        _ = Bytes(keys, "auth", 16);
        if (point[0] != 4) { throw Bad(); }
        try
        {
            using var key = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..65] },
            });
        }
        catch (CryptographicException) { throw Bad(); }
        catch (PlatformNotSupportedException) { throw Bad(); }
        string? device = null;
        if (data.TryGetProperty("device", out var name) && name.ValueKind != JsonValueKind.Null)
        {
            device = Text(data, "device", 64, allowEmpty: true);
            if (device.Any(char.IsControl)) { throw Bad(); }
        }
        return new(operation, target, token, Text(data, "keyId", 43),
            new PushKeys(Text(keys, "p256dh", 87), Text(keys, "auth", 22)), device);
    }

    static string ReadTarget(JsonElement data)
    {
        var target = Text(data, "target", 64);
        if (target[0] is < 'a' or > 'z' || target.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) { throw Bad(); }
        return target;
    }

    static void Shape(JsonElement data, params string[] fields)
    {
        if (data.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(data.GetRawText()) > 4096) { throw Bad(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            string name;
            try { name = property.Name; }
            catch (InvalidOperationException) { throw Bad(); }
            if (!fields.Contains(name, StringComparer.Ordinal) || !seen.Add(name)) { throw Bad(); }
        }
    }

    static string Text(JsonElement data, string name, int maxBytes, bool allowEmpty = false)
    {
        if (!data.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) { throw Bad(); }
        string value;
        try { value = property.GetString()!; }
        catch (InvalidOperationException) { throw Bad(); }
        if ((!allowEmpty && value.Length == 0) || Encoding.UTF8.GetByteCount(value) > maxBytes) { throw Bad(); }
        return value;
    }

    static byte[] Bytes(JsonElement data, string name, int size)
    {
        var length = (size * 4 + 2) / 3;
        var value = Text(data, name, length);
        if (value.Length != length || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) { throw Bad(); }
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - length % 4) % 4));
        if (bytes.Length != size || Attachments.Base64Url(bytes) != value) { throw Bad(); }
        return bytes;
    }

    static JsonException Bad() => new("invalid native push request");
}
