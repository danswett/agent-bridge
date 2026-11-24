using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Sdk;

namespace AgentBridge.App.Tests;

// The quarantine list for this assembly (fixes-main, Part of #4104): a test that holds main red and is not being fixed
// this hour stays in the source and runs nowhere, skipped with a reason that names the tracking issue and an expiry
// (the quality README, Flakes). Each entry is a skip whose reason starts "quarantined"; this list is the only place that
// says which, and it stays small: adding an entry is a change to this file, reviewed as one.
public sealed class QuarantineTests
{
    // Method names (class.method), each with the rows or cases skipped. Expiry is in the reason, not checked against the clock:
    // a quarantine that lapses must not turn main red by itself.
    // Every WindowsMiniDumpTests test that calls MiniDumpWriteDump on the test process itself, which Microsoft warns can
    // deadlock while other threads run, and hangs the test host: the real dump, the keep-newest one, the low-disk rows that write.
    static readonly string[] Expected =
    [
        "WindowsMiniDumpTests.A_dump_is_a_real_minidump_named_for_its_time_and_process_and_far_smaller_than_a_full_one",
        "WindowsMiniDumpTests.Only_the_newest_few_dumps_are_kept",
        "WindowsMiniDumpTests.With_less_than_a_gigabyte_of_free_disk_no_dump_is_written_and_the_reason_is_said",
    ];
    const int Largest = 3;

    static List<(string Test, string Reason)> Quarantined() =>
        typeof(QuarantineTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes(true).Select(a => a switch { FactAttribute f => f.Skip, DataAttribute d => d.Skip, _ => null })
                .Where(skip => skip is not null && skip.StartsWith("quarantined", StringComparison.Ordinal))
                .Select(skip => (Test: $"{m.DeclaringType!.Name}.{m.Name}", Reason: skip!)))
            .ToList();

    [Fact]
    public void The_quarantine_list_is_the_declared_tests_and_stays_small()
    {
        var found = Quarantined();
        Assert.Equal(Expected.Order(StringComparer.Ordinal), found.Select(q => q.Test).Distinct().Order(StringComparer.Ordinal));
        Assert.InRange(found.Select(q => q.Test).Distinct().Count(), 1, Largest);
    }

    [Fact]
    public void Each_quarantine_names_its_tracking_issue_and_an_expiry()
    {
        var found = Quarantined();
        Assert.NotEmpty(found);
        Assert.All(found, q =>
        {
            Assert.Contains("#4104", q.Reason, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"until 20\d\d-\d\d-\d\d"), q.Reason);
        });
    }
}
