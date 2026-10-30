using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Core.Usage;

// A further Copilot login this machine reports beside the one Copilot is signed in as, and its
// place: 2 for the first listed, as agent-ha-bridge numbers its usage_copilot_<slot> sensors.
public sealed record CopilotAccount(int Slot, string Login);

// config.json's usage section (docs/host-config.md), agent-ha-bridge's usage.* kept to its
// meaning (hooks/daemon-usage.ps1):
//
//   publish             false stops every vendor call; nothing is reported
//   intervalSeconds     the pace while a session of an agent is open here (120)
//   idleIntervalSeconds the pace while none is (900), never quicker than intervalSeconds
//   copilotAccounts     up to three further GitHub logins, each read with the token gh holds for
//                       exactly that login, each its own row
//
// What is wrong is said, and read in the direction that does least: publish that is not true or
// false, or a section that is not an object, stops collection (it may have been meant as off);
// a pace that is not a number in range keeps the default; an entry of copilotAccounts that is not
// a login is skipped and keeps its place, so the logins after it keep their slots.
public sealed partial record UsageSettings
{
    public const int AccountSlots = 3;
    public const int MinSeconds = 30;
    public const int MaxSeconds = 86400;

    public static readonly UsageSettings Default = new();

    public bool Publish { get; init; } = true;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(900);
    public IReadOnlyList<CopilotAccount> CopilotAccounts { get; init; } = [];

    public static (UsageSettings Settings, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        var problems = new List<string>();
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Default, problems); }
        if (s.ValueKind != JsonValueKind.Object)
        {
            problems.Add("usage is not an object, so no usage is read");
            return (Default with { Publish = false }, problems);
        }
        var settings = Default;
        if (s.TryGetProperty("publish", out var publish) && publish.ValueKind != JsonValueKind.Null)
        {
            if (publish.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { Publish = publish.ValueKind == JsonValueKind.True }; }
            else
            {
                problems.Add("usage.publish is not true or false, so no usage is read");
                settings = settings with { Publish = false };
            }
        }
        var interval = Seconds(s, "intervalSeconds", settings.Interval, problems);
        var idle = Seconds(s, "idleIntervalSeconds", settings.IdleInterval, problems);
        // A machine doing nothing asking more often than one doing something would be backwards.
        settings = settings with { Interval = interval, IdleInterval = idle < interval ? interval : idle };
        if (s.TryGetProperty("copilotAccounts", out var accounts) && accounts.ValueKind != JsonValueKind.Null)
        {
            settings = settings with { CopilotAccounts = Accounts(accounts, problems) };
        }
        return (settings, problems);
    }

    static TimeSpan Seconds(JsonElement s, string name, TimeSpan fallback, List<string> problems)
    {
        if (!s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return fallback; }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var seconds) && seconds >= MinSeconds && seconds <= MaxSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        problems.Add($"usage.{name} is not a number of seconds from {MinSeconds} to {MaxSeconds}, so {fallback.TotalSeconds.ToString(CultureInfo.InvariantCulture)} is used");
        return fallback;
    }

    // Named, never discovered: a login is reported only because somebody listed it. The Nth entry
    // has slot N+1 whatever becomes of the others, so an account keeps its slot when another entry
    // is added, mended or taken out. An entry is named by its place, never repeated back: one
    // shaped like a token would otherwise reach the log, and the host's log names no account.
    static IReadOnlyList<CopilotAccount> Accounts(JsonElement list, List<string> problems)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            problems.Add("usage.copilotAccounts is not a list of logins, so no further account is read");
            return [];
        }
        var accounts = new List<CopilotAccount>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var entry in list.EnumerateArray())
        {
            index++;
            var place = $"usage.copilotAccounts entry {index}";
            var login = entry.ValueKind == JsonValueKind.String ? entry.GetString()!.Trim() : "";
            if (TokenShaped().IsMatch(login)) { problems.Add($"{place} looks like a token, not a login, so it is not used"); continue; }
            if (!LoginShaped().IsMatch(login)) { problems.Add($"{place} is not a GitHub login"); continue; }
            if (index > AccountSlots) { problems.Add($"{place} is past the {AccountSlots} further logins there are rows for"); continue; }
            if (!seen.Add(login)) { problems.Add($"{place} repeats an earlier login"); continue; }
            accounts.Add(new CopilotAccount(index + 1, login));
        }
        return accounts;
    }

    [GeneratedRegex("^(?:gh[opusr]_|github_pat_)")]
    private static partial Regex TokenShaped();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex LoginShaped();
}
