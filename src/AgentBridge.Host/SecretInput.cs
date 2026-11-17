using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Host;

internal static class SecretInput
{
    public static string? Read(TextWriter echo, Func<ConsoleKeyInfo>? readKey = null)
    {
        if (readKey is null && Console.IsInputRedirected)
        {
            var value = Console.In.ReadLine();
            echo.WriteLine(new string('*', value?.Length ?? 0));
            return value;
        }
        var text = new StringBuilder();
        while (true)
        {
            var key = readKey?.Invoke() ?? Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) { text.Length--; echo.Write("\b \b"); }
            }
            else if (!char.IsControl(key.KeyChar) || char.IsWhiteSpace(key.KeyChar))
            {
                text.Append(key.KeyChar);
                echo.Write('*');
            }
        }
        echo.WriteLine();
        return text.ToString();
    }

    public static void Received(string value, TextWriter output, bool fingerprint = false)
    {
        output.Write($"Received {value.Length} characters (masked); whitespace: {(value.Any(char.IsWhiteSpace) ? "yes" : "no")}");
        if (fingerprint) { output.Write("; sha256: " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8]); }
        output.WriteLine(".");
    }

    public static string? HaFormatProblem(string token)
    {
        if (token.Any(char.IsWhiteSpace)) { return "whitespace is not allowed, including at the ends"; }
        if (!token.StartsWith("eyJ", StringComparison.Ordinal)) { return "expected the Home Assistant JWT prefix"; }
        if (token.Length is < 100 or > 4096) { return "expected a token length from 100 to 4096 characters"; }
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Length % 4 == 1 ||
            p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) || parts[2].Length != 43)
        { return "expected three base64url JWT segments with a 43-character signature"; }
        try
        {
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            if (header.RootElement.ValueKind != JsonValueKind.Object ||
                !header.RootElement.TryGetProperty("alg", out var algorithm) || algorithm.ValueKind != JsonValueKind.String ||
                algorithm.GetString() != "HS256") { return "expected a Home Assistant HS256 JWT header"; }
            if (payload.RootElement.ValueKind != JsonValueKind.Object) { return "expected a JSON object payload"; }
        }
        catch (Exception e) when (e is FormatException or JsonException) { return "JWT segments must contain encoded JSON objects"; }
        return null;
    }

    static byte[] Decode(string part) =>
        Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/') + new string('=', (4 - part.Length % 4) % 4));
}
