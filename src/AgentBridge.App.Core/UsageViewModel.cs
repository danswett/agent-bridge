using System.Collections.ObjectModel;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The usage page: agent-ha-bridge's usage card, as the PWA draws it (pwa/js/usage.js): one
// row per allowance, its windows as bars, amber at 75 % and red at 90 %, where even pace
// would put the spend, when each resets and how old the reading is.
public sealed class UsageViewModel : Observable
{
    readonly TimeProvider time;
    readonly UsageCache? cache;
    UsageReport report = UsageReport.Empty;
    UsageReport live = UsageReport.Empty;
    string? account;
    string? here;
    string summary = "No usage reported yet";

    // cache: the last reading of each allowance kept between runs (#172), shown when no machine
    // online offers it now.
    public UsageViewModel(TimeProvider? time = null, UsageCache? cache = null)
    {
        this.time = time ?? TimeProvider.System;
        this.cache = cache;
    }

    public ObservableCollection<UsageRowViewModel> Rows { get; } = [];
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public bool IsEmpty => Rows.Count == 0;

    // next: every machine's readings merged, each marked with the machine that took it. here: this
    // machine's device id, whose own readings need no "read on". account: the account this
    // machine's host is signed in to, "" signed out, null not known yet (UsageCache.Merge).
    public void Update(UsageReport next, string? here = null, string? account = null)
    {
        (live, this.account, this.here) = (next, account, here);
        report = cache?.Merge(next, account) ?? next;
        Refresh();
    }

    // A machine forgotten from the account's roster: what it read is shown and kept no more (#50).
    public void Forget(string machineId)
    {
        if (cache is null) { return; }
        cache.Forget(machineId);
        report = cache.Merge(live, account);
        Refresh();
    }

    // Machines online now: one forgotten earlier is the account's again.
    public void Present(IEnumerable<string> machineIds) => cache?.Present(machineIds);

    // Also on a timer: ages and "resets in" move on without any update.
    public void Refresh()
    {
        var now = time.GetUtcNow();
        Rows.Clear();
        // One row per allowance even from one machine: a further Copilot account listed that is, or
        // becomes, the signed-in one (usage.copilotAccounts) is one allowance read twice. Where
        // readings are kept, none past their 14 days, whether or not anything has been heard since.
        var oldest = now - UsageCache.Kept;
        foreach (var a in UsageMerge.Merge([report]).Agents.Where(a => cache is null || a.MeasuredAt >= oldest).OrderByDescending(a => Headline(a) ?? 0)) { Rows.Add(new UsageRowViewModel(a, now, here)); }
        var figures = Rows.Where(r => r.Headline is not null).Select(r => $"{r.Label} {Math.Round(r.Headline!.Value)}%").ToList();
        Summary = figures.Count > 0 ? string.Join(" · ", figures) : "No usage reported yet";
        Raise(nameof(IsEmpty));
    }

    public static double? Headline(AgentUsage a) => a.Windows.Count > 0 ? a.Windows.Max(w => w.Percent) : null;

    public static string Severity(double percent) => percent >= 90 ? "crit" : percent >= 75 ? "warn" : "";
}

public sealed class UsageRowViewModel
{
    public UsageRowViewModel(AgentUsage a, DateTimeOffset now, string? here = null)
    {
        Agent = a.Agent;
        Label = a.Label;
        Detail = string.Join(" · ", new[] { a.Account ?? (a.AccountKey is { Length: >= 4 } k ? $"#{k[..4]}" : null), a.Plan }.Where(s => !string.IsNullOrEmpty(s)));
        Age = Words.ReadingAgo(a.MeasuredAt, now);
        Stale = now - a.MeasuredAt > TimeSpan.FromHours(1);
        // Which machine took it, unless this one did: a reading kept, or passed on by another host,
        // may be from a machine that is offline now (#172).
        ReadOn = a.ReadBy is { } by && by.Id != here ? $"read on {by.Name}" : null;
        Headline = UsageViewModel.Headline(a);
        Windows = [.. a.Windows.Select(w => new UsageWindowViewModel(w, now))];
        // A failed read beside a fresh reading is noise; with no reading, or an old one, it is
        // all there is to say.
        Error = !string.IsNullOrEmpty(a.Error) && !(a.Windows.Count > 0 && !Stale) ? a.Error : null;
    }

    public AgentKind Agent { get; }
    public string Label { get; }
    public string Detail { get; }
    public string Age { get; }
    public string? ReadOn { get; }
    public bool Stale { get; }
    public double? Headline { get; }
    public IReadOnlyList<UsageWindowViewModel> Windows { get; }
    public string? Error { get; }
}

public sealed class UsageWindowViewModel
{
    public UsageWindowViewModel(UsageWindow w, DateTimeOffset now)
    {
        Label = w.Label;
        Percent = Math.Clamp(w.Percent, 0, 100);
        PercentText = $"{w.Percent:0.#}%";
        Severity = UsageViewModel.Severity(w.Percent);
        var used = Words.Amount(w.Used);
        var limit = Words.Amount(w.Limit);
        AmountText = used.Length > 0 && limit.Length > 0 ? $"{used} / {limit}{(w.Unit is { } u ? $" {u}" : "")}" : "";
        var resets = Words.Until(w.ResetsAt, now);
        ResetText = resets.Length > 0 ? $"resets {resets}" : "";
        if (w.StartsAt is { } start && w.ResetsAt is { } end && end > start)
        {
            Elapsed = Math.Round(1000 * Math.Clamp((now - start) / (end - start), 0, 1)) / 10;
            var ahead = w.Percent - Elapsed.Value;
            PaceText = ahead > 10 ? $"{Math.Round(ahead)}% ahead of pace" : ahead < -10 ? $"{Math.Round(-ahead)}% under pace" : "on pace";
            AheadOfPace = ahead > 10;
            EvenPaceText = $"│ even pace {Math.Round(Elapsed.Value)}%";
        }
    }

    public string Label { get; }
    public double Percent { get; }
    public string PercentText { get; }
    public string Severity { get; }
    public string AmountText { get; }
    public string ResetText { get; }
    // How far through its window we are, 0-100, or null when either end is unknown.
    public double? Elapsed { get; }
    public string? PaceText { get; }
    public bool AheadOfPace { get; }
    public string? EvenPaceText { get; }
}
