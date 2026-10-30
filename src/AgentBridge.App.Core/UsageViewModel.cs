using System.Collections.ObjectModel;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The usage page: agent-ha-bridge's usage card, as the PWA draws it (pwa/js/usage.js): one
// row per allowance, its windows as bars, amber at 75 % and red at 90 %, where even pace
// would put the spend, when each resets and how old the reading is.
public sealed class UsageViewModel : Observable
{
    readonly TimeProvider time;
    UsageReport report = UsageReport.Empty;
    string summary = "No usage reported yet";

    public UsageViewModel(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public ObservableCollection<UsageRowViewModel> Rows { get; } = [];
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public bool IsEmpty => Rows.Count == 0;

    public void Update(UsageReport next)
    {
        report = next;
        Refresh();
    }

    // Also on a timer: ages and "resets in" move on without any update.
    public void Refresh()
    {
        var now = time.GetUtcNow();
        Rows.Clear();
        foreach (var a in Merge(report.Agents).OrderByDescending(a => Headline(a) ?? 0)) { Rows.Add(new UsageRowViewModel(a, now)); }
        var figures = Rows.Where(r => r.Headline is not null).Select(r => $"{r.Label} {Math.Round(r.Headline!.Value)}%").ToList();
        Summary = figures.Count > 0 ? string.Join(" · ", figures) : "No usage reported yet";
        Raise(nameof(IsEmpty));
    }

    // One row per allowance, as the PWA's mergeUsage has it: a further Copilot account listed that
    // is, or becomes, the signed-in one (usage.copilotAccounts) is one allowance read twice. A
    // reading outranks a failure however new, then the latest wins, and a failure newer than the
    // reading lends it its error.
    public static IReadOnlyList<AgentUsage> Merge(IEnumerable<AgentUsage> agents)
    {
        var rows = new List<AgentUsage>();
        foreach (var group in agents.GroupBy(a => (a.Agent, a.AccountKey is { Length: > 0 } k ? k : (a.Account ?? "").ToLowerInvariant())))
        {
            var reading = group.Where(a => a.Windows.Count > 0).MaxBy(a => a.MeasuredAt);
            var failure = group.Where(a => a.Windows.Count == 0).MaxBy(a => a.MeasuredAt);
            rows.Add(reading is null ? failure!
                : failure is { Error: { Length: > 0 } error } && failure.MeasuredAt > reading.MeasuredAt ? reading with { Error = error } : reading);
        }
        return rows;
    }

    public static double? Headline(AgentUsage a) => a.Windows.Count > 0 ? a.Windows.Max(w => w.Percent) : null;

    public static string Severity(double percent) => percent >= 90 ? "crit" : percent >= 75 ? "warn" : "";
}

public sealed class UsageRowViewModel
{
    public UsageRowViewModel(AgentUsage a, DateTimeOffset now)
    {
        Agent = a.Agent;
        Label = a.Label;
        Detail = string.Join(" · ", new[] { a.Account ?? (a.AccountKey is { Length: >= 4 } k ? $"#{k[..4]}" : null), a.Plan }.Where(s => !string.IsNullOrEmpty(s)));
        Age = Words.ReadingAgo(a.MeasuredAt, now);
        Stale = now - a.MeasuredAt > TimeSpan.FromHours(1);
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
