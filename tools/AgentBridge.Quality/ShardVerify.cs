using System.Diagnostics;

namespace AgentBridge.Quality;

/// <summary>
/// The verify-only CI gate for the test shards (S4, #4104). It runs in the unprivileged build job (no secrets, no write token), after the build: for every built test assembly it
/// reads the test METHODS from metadata (<see cref="TestMethodScanner"/>) and asks the plan (<see cref="ShardPlanFile.RunningShards"/>, VSTest's own containment rule, case ignored)
/// which shards would run each. Exactly one, or the pull request fails, naming the method: a new look-alike of a listed class (<c>XAnswerFlowTests</c> beside <c>AgentAnswerFlowTests</c>)
/// that would run twice, or a method no shard runs. Fail closed: an unreadable plan, a missing or unloadable assembly, one with no tests, or no assemblies at all, is a failure.
/// </summary>
internal static class ShardVerify
{
    /// <summary>The methods that would run in other than exactly one shard, each with the shards that would run it.</summary>
    public static IReadOnlyList<string> Check(ShardPlanFile plan, string project, IEnumerable<string> methods) =>
        [.. methods.Select(m => (Method: m, Shards: plan.RunningShards(project, m).ToArray())).Where(x => x.Shards.Length != 1)
            .Select(x => $"{project}: {x.Method} would run in shards [{string.Join(", ", x.Shards)}], not exactly one")];

    public static int Run(string planFile, IReadOnlyList<string> dlls, TextWriter output, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        ShardPlanFile plan;
        try { plan = ShardPlanFile.Parse(File.ReadAllText(planFile)); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"FAIL: the shard plan cannot be read: {e.Message}");
            return 1;
        }
        if (dlls.Count == 0)
        {
            output.WriteLine("FAIL: no test assemblies were given (--dll), so nothing was verified");
            return 1;
        }
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var dll in dlls)
        {
            var project = Path.GetFileNameWithoutExtension(dll);
            seen.Add(project);
            if (clock.Elapsed > timeout) { problems.Add($"{project}: not verified, the {timeout.TotalSeconds:0}-second time box ran out"); continue; }
            IReadOnlyList<string> methods;
            try { methods = TestMethodScanner.Scan(dll); }
            catch (Exception e) when (e is IOException or BadImageFormatException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                problems.Add($"{project}: cannot be read: {e.Message}");
                continue;
            }
            if (methods.Count == 0) { problems.Add($"{project}: no test methods found in {dll}"); continue; }
            total += methods.Count;
            problems.AddRange(Check(plan, project, methods));
        }
        problems.AddRange(plan.Projects.Where(p => !seen.Contains(p)).Select(p => $"{p}: named in the plan but its assembly was not given"));
        foreach (var problem in problems) { output.WriteLine($"FAIL: {problem}"); }
        if (problems.Count == 0) { output.WriteLine($"ok: {total} test methods in {dlls.Count} assemblies each run in exactly one of {plan.Shards} shards"); }
        return problems.Count == 0 ? 0 : 1;
    }
}
