using System.Globalization;

namespace AgentBridge.App.Core;

// Short times and counts for a status line, worded as the PWA's (pwa/js/ui/time.js and
// pwa/js/usage.js), so the app and the phone read the same.
public static class Words
{
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var s = (long)Math.Floor((now - at).TotalSeconds);
        return s < 10 ? "just now" : $"{Span(s)} ago";
    }

    public static string Duration(DateTimeOffset since, DateTimeOffset now) => Span(Math.Max(0, (long)Math.Floor((now - since).TotalSeconds)));

    static string Span(long s)
    {
        if (s < 60) { return $"{s}s"; }
        var m = s / 60;
        if (m < 60) { return $"{m}m"; }
        var h = m / 60;
        if (h < 48) { return $"{h}h {m % 60}m"; }
        return $"{h / 24}d";
    }

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // A row's age (ui.md §6.1, the mock-ups' "now", "32m", "10h"): how long since the session
    // last changed, in its largest whole unit.
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var s = Math.Max(0, (long)Math.Floor((now - at).TotalSeconds));
        if (s < 60) { return "now"; }
        var m = s / 60;
        if (m < 60) { return $"{m}m"; }
        var h = m / 60;
        return h < 48 ? $"{h}h" : $"{h / 24}d";
    }

    // A path or command cut in the middle to at most `most` characters, keeping its start and its
    // end, the part people tell paths apart by (#214): never broken mid-word onto another line.
    public static string Middle(string? text, int most)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= most || most < 5) { return text ?? ""; }
        var tail = (most - 1) / 2 + (most - 1) % 2;
        var head = most - 1 - tail;
        return $"{text[..head]}\u2026{text[^tail..]}";
    }

    // "in 25d" / "in 4h" / "in 12m"; "due" once the window has rolled over.
    public static string Until(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) { return ""; }
        var seconds = (long)Math.Round((t - now).TotalSeconds);
        if (seconds <= 0) { return "due"; }
        if (seconds >= 86400) { return $"in {Math.Round(seconds / 86400.0)}d"; }
        if (seconds >= 3600) { return $"in {Math.Round(seconds / 3600.0)}h"; }
        return $"in {Math.Max(1, Math.Round(seconds / 60.0))}m";
    }

    // The usage card's own "ago", coarser than a session's.
    public static string ReadingAgo(DateTimeOffset at, DateTimeOffset now)
    {
        var seconds = Math.Round((now - at).TotalSeconds);
        if (seconds < 90) { return "just now"; }
        if (seconds < 3600) { return $"{Math.Round(seconds / 60)}m ago"; }
        if (seconds < 86400) { return $"{Math.Round(seconds / 3600)}h ago"; }
        return $"{Math.Round(seconds / 86400)}d ago";
    }

    public static string Amount(double? value) => value is { } v && double.IsFinite(v) ? Math.Round(v).ToString("#,0", CultureInfo.GetCultureInfo("en-US")) : "";

    public static string Size(long n) => n < 1024 ? $"{n} bytes" : n < 1024 * 1024 ? $"{Math.Round(n / 1024.0)} KiB" : $"{n / (1024.0 * 1024):F1} MiB";

    public static string LastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) { return ""; }
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : "";
    }

    // A sentence ends with its stop: a reason from the host may or may not have one.
    public static string Sentence(string? s) => string.IsNullOrEmpty(s) || ".!?".Contains(s[^1]) ? s ?? "" : s + ".";
}
