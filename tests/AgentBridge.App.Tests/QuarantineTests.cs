using System.Reflection;
using Xunit;

namespace AgentBridge.App.Tests;

// The suspected causes of the App.Tests test-host hang (fixes-main, Part of #4104). They stay in the suite and keep their
// coverage, but run ALONE: xunit runs a collection defined with DisableParallelization after the parallel ones have finished,
// so a real MiniDumpWriteDump of this process (which suspends its threads, and Microsoft warns can deadlock) never happens
// while other tests run, and RunCompletionTests (which sets a process-wide environment variable and compiles with Roslyn behind a
// static lazy) never overlaps them. This removes suspected causes; the root cause of the hang is not known to be among them.
// This list is the only place that says which, and it stays small: adding a class is a change to this file, reviewed as one.
public sealed class QuarantineTests
{
    static readonly string[] Expected = ["RunCompletionTests", "WindowsMiniDumpTests"];
    const int Largest = 3;

    static List<string> RunningAlone()
    {
        var types = typeof(QuarantineTests).Assembly.GetTypes();
        var alone = types.Select(t => t.GetCustomAttribute<CollectionDefinitionAttribute>())
            .Where(d => d is { DisableParallelization: true })
            .Select(d => d!.Name)
            .ToHashSet();
        return types.Where(t => t.GetCustomAttribute<CollectionAttribute>() is { } c && alone.Contains(c.Name))
            .Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void The_suspected_hang_causes_run_alone_and_nothing_else_does()
    {
        var found = RunningAlone();
        Assert.Equal(Expected, found);
        Assert.InRange(found.Count, 1, Largest);
    }

    // Kept in the suite, not skipped: a skipped test is coverage lost.
    [Fact]
    public void Nothing_in_the_assembly_is_skipped_as_quarantined()
    {
        var skipped = typeof(QuarantineTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes(true).Select(a => a switch { FactAttribute f => f.Skip, Xunit.Sdk.DataAttribute d => d.Skip, _ => null })
                .Where(skip => skip is not null && skip.StartsWith("quarantined", StringComparison.Ordinal))
                .Select(_ => $"{m.DeclaringType!.Name}.{m.Name}"))
            .Distinct().ToList();
        Assert.Empty(skipped);
    }
}
