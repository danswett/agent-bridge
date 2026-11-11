using System.Text.Json;

namespace AgentBridge.Rehearsal;

internal enum ConformanceVerdict { Pass, Fail, NotSupported, Missing }
internal sealed record ConformanceRun(string Client, string Version, int ProtocolVersion, string ImplementationHead, string RunId);
internal sealed record ConformanceCapability(string Name, bool Live = false);
internal sealed record ConformanceResult(ConformanceRun Run, string Capability, ConformanceVerdict Verdict, string Reason, Uri? Evidence = null);

internal static class ClientConformance
{
    public static IReadOnlyList<ConformanceResult> Build(IReadOnlyList<ConformanceRun> runs,
        IReadOnlyList<ConformanceCapability> capabilities, IEnumerable<ConformanceResult> results)
    {
        if (runs.Count == 0 || capabilities.Count == 0)
            throw new ArgumentException("Required clients and capabilities must not be empty.");
        foreach (var run in runs)
        {
            if (string.IsNullOrWhiteSpace(run.Client) || string.IsNullOrWhiteSpace(run.Version)
                || string.IsNullOrWhiteSpace(run.RunId) || run.ProtocolVersion <= 0
                || run.ImplementationHead.Length != 40 || !run.ImplementationHead.All(char.IsAsciiHexDigit))
                throw new ArgumentException("Every client needs a version, protocol, full implementation SHA and run identifier.");
        }
        if (runs.Select(r => (r.Client, r.Version)).Distinct().Count() != runs.Count)
            throw new ArgumentException("A client version must identify exactly one implementation in the matrix.");
        foreach (var capability in capabilities)
            ArgumentException.ThrowIfNullOrWhiteSpace(capability.Name);
        var cases = capabilities.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var expected = runs.SelectMany(run => capabilities.Select(c => (Run: run, Capability: c.Name))).ToArray();
        var rows = expected.ToDictionary(key => key,
            key => new ConformanceResult(key.Run, key.Capability, ConformanceVerdict.Missing, "No result recorded."));
        var seen = new HashSet<(ConformanceRun, string)>();
        foreach (var result in results)
        {
            var key = (result.Run, result.Capability);
            if (!rows.ContainsKey(key) || !seen.Add(key))
                throw new ArgumentException("Duplicate result or result outside the required client/capability matrix.");
            if (!Enum.IsDefined(result.Verdict) || string.IsNullOrWhiteSpace(result.Reason))
                throw new ArgumentException("Every result needs a known verdict and a reason.");
            rows[key] = result.Verdict == ConformanceVerdict.Pass && cases[result.Capability].Live
                && result.Evidence is not { IsAbsoluteUri: true, Scheme: "https" or "http" }
                ? result with { Verdict = ConformanceVerdict.Missing, Reason = "Live step has no recorded run link." }
                : result;
        }
        return expected.Select(key => rows[key]).ToArray();
    }

    public static IEnumerable<string> JsonLines(IEnumerable<ConformanceResult> rows)
        => rows.Select(row => JsonSerializer.Serialize(row, Evidence.Json));
}
