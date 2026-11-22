using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

internal sealed record QueueTarget(int ProcessId, long StartedAt, string ConsoleId, uint InputMode,
    string CliPath, string CliVersion, string CliHash, string InjectorPath, string InjectorVersion, string InjectorHash, string Protocol)
{
    internal static QueueTarget? Decode(string? line)
    {
        var p = line?.Split(' ');
        if (p is not { Length: 12 } || p[0] != "identity"
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
            || !long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started <= 0
            || p[3].Split(':') is not [var window, var owner, var consoleStart]
            || !ulong.TryParse(window, NumberStyles.None, CultureInfo.InvariantCulture, out var hwnd) || hwnd == 0
            || !uint.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var consolePid) || consolePid == 0
            || !long.TryParse(consoleStart, NumberStyles.None, CultureInfo.InvariantCulture, out var consoleCreated) || consoleCreated <= 0
            || !uint.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out var mode)
            || !Hash(p[7]) || !Hash(p[10])) { return null; }
        try
        {
            string Text(int i) => new UTF8Encoding(false, true).GetString(Convert.FromBase64String(p[i]));
            var target = new QueueTarget(pid, started, p[3], mode, Text(5), Text(6), p[7], Text(8), Text(9), p[10], Text(11));
            return new[] { target.CliPath, target.CliVersion, target.InjectorPath, target.InjectorVersion }.Any(string.IsNullOrWhiteSpace)
                || target.Protocol != KeyPlan.Header ? null : target;
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { return null; }
    }

    static bool Hash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}

internal enum QueueCapability { Unknown, Verified, Unsupported }

// Inactive source seam: only a future authorized controller may supply capture and a
// cursor-bound raw transcript reader for this launched target. No delivery path calls this.
internal sealed class QueueQualification
{
    internal sealed class Challenge(string text) { public string Text { get; } = text; }
    readonly Func<Task<string?>> capture;
    readonly Func<IReadOnlyList<string>?> read;
    readonly TimeProvider clock;
    readonly QueueTarget? target;
    readonly SemaphoreSlim gate = new(1, 1);
    Challenge? pending;
    long issued;
    TimeSpan lifetime;
    bool working;
    internal QueueTarget? Snapshot => target;

    QueueQualification(Func<Task<string?>> capture, Func<IReadOnlyList<string>?> read, TimeProvider clock, QueueTarget? target)
        => (this.capture, this.read, this.clock, this.target) = (capture, read, clock, target);

    internal static async Task<QueueQualification> CaptureAtLaunchAsync(Func<Task<string?>> capture, Func<IReadOnlyList<string>?> read, TimeProvider clock)
        => new(capture, read, clock, QueueTarget.Decode(await capture()));

    internal async Task<Challenge?> IssueAsync(TimeSpan expiresAfter, Challenge? approvedChallenge = null)
    {
        await gate.WaitAsync();
        try
        {
            pending = null;
            if (target is null || expiresAfter <= TimeSpan.Zero || QueueTarget.Decode(await capture()) != target
                || !Read(null, out _) || !working || QueueTarget.Decode(await capture()) != target) { return null; }
            lifetime = expiresAfter;
            issued = clock.GetTimestamp();
            return pending = approvedChallenge ?? new Challenge("agent-bridge-qualify:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        }
        finally { gate.Release(); }
    }

    internal async Task<QueueCapability> ObserveAsync(Challenge challenge)
    {
        await gate.WaitAsync();
        try
        {
            var token = pending;
            pending = null; // Consume before any await, including failed and foreign attempts.
            if (token is null || !ReferenceEquals(token, challenge) || Expired() || target is null
                || QueueTarget.Decode(await capture()) != target || !Read(token, out var matched) || !matched
                || QueueTarget.Decode(await capture()) != target || Expired()) { return QueueCapability.Unknown; }
            return QueueCapability.Verified;
        }
        finally { gate.Release(); }
    }

    bool Expired() => clock.GetElapsedTime(issued) >= lifetime;

    bool Read(Challenge? challenge, out bool matched)
    {
        matched = false;
        if (read() is not { } rows) { working = false; return false; }
        foreach (var row in rows)
        {
            try
            {
                using var json = JsonDocument.Parse(row);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !Unique(root)) { working = false; return false; }
                if (root.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object && !Unique(payload))
                { working = false; return false; }
                if (root.TryGetProperty("agentId", out var agent) && agent.ValueKind != JsonValueKind.Null) { continue; }
                var type = Text(root, "type");
                if (type == "assistant.turn_start") { working = true; }
                else if (type is "assistant.turn_end" or "session.resume" or "session.shutdown" or "abort") { working = false; }
                else if (type == "user.message" && challenge is not null
                    && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && Text(data, "content") == challenge.Text)
                {
                    if (matched || !working || Text(data, "delivery") != "queued"
                        || data.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null
                            && (source.ValueKind != JsonValueKind.String || source.GetString() != "user"))
                    { working = false; return false; }
                    matched = true;
                }
            }
            catch (JsonException) { working = false; return false; }
        }
        return true;
    }

    static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    static bool Unique(JsonElement value) =>
        value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
}
