using System.Text.Json;

namespace AgentBridge.Host.Delivery;

// Which questions this host has already typed an answer into, kept on disk so that no
// question is ever typed into twice: not by a second request for it, nor after the host
// restarts. agent-ha-bridge holds the same line (Start-CopilotDecisionAttempt for its
// questions, DeliveryAttempted on a Codex approval marker): once a key may have reached a
// prompt, nobody can say what it holds, and a second answer would land on top of the first.
//
// A claim is written and flushed before anything is typed, and released only when the
// worker reports that nothing reached the console. A ledger that cannot be read or written
// refuses every claim: unknown history is not an empty one.
public sealed class AttemptLedger
{
    // Long past any prompt: Copilot and Claude question ids and Codex generations are
    // never reused, so an entry this old can only ever match nothing.
    static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    readonly string? path;
    readonly TimeProvider time;
    readonly Lock gate = new();
    Dictionary<string, DateTimeOffset>? attempts;
    string? broken;

    // path: where the ledger lives, in the host's own profile folder; null keeps it in memory
    // for the life of the process only.
    public AttemptLedger(string? path, TimeProvider? time = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
    }

    public static string Key(string agent, string sessionId, string questionId) => $"{agent}\u001f{sessionId}\u001f{questionId}";

    // Null when the claim is now held; otherwise why no answer may be typed.
    public string? Claim(string key)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return problem; }
            if (attempts!.ContainsKey(key)) { return "an answer was already typed into this prompt once, and is never typed again; check at the machine"; }
            attempts[key] = time.GetUtcNow();
            if (Save() is { } failed)
            {
                attempts.Remove(key);
                return failed;
            }
            return null;
        }
    }

    // Nothing reached the console, so the prompt may be answered again. A release that cannot
    // be saved leaves the claim standing, which only ever errs towards typing nothing.
    public void Release(string key)
    {
        lock (gate)
        {
            if (attempts is null || !attempts.Remove(key, out var at)) { return; }
            if (Save() is not null) { attempts[key] = at; }
        }
    }

    string? Load()
    {
        if (attempts is not null) { return null; }
        if (broken is not null) { return broken; }
        if (path is null || !File.Exists(path))
        {
            attempts = new(StringComparer.Ordinal);
            return null;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            attempts = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(stream) is { } read
                ? new Dictionary<string, DateTimeOffset>(read, StringComparer.Ordinal)
                : throw new JsonException("empty");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            broken = $"the host's record of answers already typed could not be read ({e.Message}), so nothing is typed; answer at the machine";
            return broken;
        }
    }

    string? Save()
    {
        if (path is null) { return null; }
        var cutoff = time.GetUtcNow() - Keep;
        var all = attempts!;
        foreach (var old in all.Where(a => a.Value < cutoff).Select(a => a.Key).ToList()) { all.Remove(old); }
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, attempts);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the host could not record the attempt ({e.Message}), so nothing is typed; answer at the machine";
        }
    }
}
