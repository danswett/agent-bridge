using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentBridge.Learning;

sealed record FailureSignature(string Signature, int Occurrences);
sealed record CaptureResult(
    int Version, string RecordId, string Stage, string RunId, string Suite, string DeclaredHead,
    string InputSha256, int Results, int Passed, int Failed, int Skipped, int Unparsed,
    bool RunFailed, FailureSignature[] Failures);

static class TrxCapture
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static CaptureResult Parse(byte[] bytes, string suite, string head, string runId, string stage)
    {
        Note.CommitId(head);
        foreach (var id in new[] { suite, runId })
            if (id is null || !Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
                throw new FormatException("Suite and run ids must be simple identifiers, not paths or URLs.");
        if (stage is not ("local" or "ci")) throw new FormatException("Stage must be local or ci.");
        if (bytes.Length > 16 * 1024 * 1024) throw new FormatException("TRX exceeds 16 MiB.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != Ns + "TestRun") throw new FormatException("Expected a TRX TestRun in the 2010 namespace.");
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in root.Element(Ns + "TestDefinitions")?.Elements(Ns + "UnitTest") ?? [])
        {
            var method = test.Element(Ns + "TestMethod");
            var type = ((string?)method?.Attribute("className"))?.Split(',')[0].Trim();
            var name = (string?)method?.Attribute("name");
            var id = (string?)test.Attribute("id");
            // testName and diagnostics may contain theory arguments, paths or credentials.
            if (id is not null && Symbol(type) && Symbol(name))
                if (!definitions.TryAdd(id, type + "." + name))
                    throw new FormatException("TRX contains duplicate test definitions.");
        }
        var results = root.Element(Ns + "Results")?.Descendants(Ns + "UnitTestResult").ToList() ?? [];
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var unparsed = 0;
        var signatures = new List<string>();
        foreach (var result in results)
        {
            var outcome = (string?)result.Attribute("outcome");
            switch (outcome)
            {
                case "Passed": passed++; break;
                case "NotExecuted":
                case "Inconclusive":
                case "NotRunnable":
                    skipped++;
                    break;
                case "Failed":
                case "Error":
                case "Timeout":
                case "Aborted":
                    failed++;
                    if (definitions.TryGetValue((string?)result.Attribute("testId") ?? "", out var test))
                        signatures.Add($"{suite}|test:{test}");
                    else
                        unparsed++;
                    break;
                default: unparsed++; break;
            }
        }
        var summary = root.Element(Ns + "ResultSummary");
        var summaryOutcome = (string?)summary?.Attribute("outcome");
        var runFailed = summaryOutcome is "Failed" or "Error" or "Timeout" or "Aborted";
        if (summaryOutcome is not ("Completed" or "Passed" or "Failed" or "Error" or "Timeout" or "Aborted" or "Inconclusive" or "NotExecuted"))
            unparsed++;
        if (results.Count == 0 || (runFailed && failed == 0))
            unparsed++;
        var counters = summary?.Element(Ns + "Counters");
        if (!int.TryParse((string?)counters?.Attribute("total"), out var total) || total != results.Count)
            unparsed++;
        foreach (var outcome in new[] { "Passed", "Failed", "Error", "Timeout", "Aborted", "NotExecuted", "Inconclusive", "NotRunnable" })
        {
            var expected = results.Count(r => (string?)r.Attribute("outcome") == outcome);
            var counter = char.ToLowerInvariant(outcome[0]) + outcome[1..];
            var raw = (string?)counters?.Attribute(counter);
            if (raw is null && expected == 0) continue;
            if (!int.TryParse(raw, out var actual) || actual != expected) unparsed++;
        }
        if (unparsed > 0) signatures.AddRange(Enumerable.Repeat($"{suite}|unparsed", unparsed));
        var hash = JsonData.Hash(bytes);
        var recordId = JsonData.Hash(string.Join("\n", "trx-v1", stage, runId, suite, head, hash));
        return new(1, recordId, stage, runId, suite, head, hash, results.Count, passed, failed, skipped, unparsed,
            runFailed || failed > 0, signatures.GroupBy(s => s).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new FailureSignature(g.Key, g.Count())).ToArray());
    }

    static bool Symbol(string? value) => value is not null && value.Length <= 512
        && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_.+`<>]*$", RegexOptions.CultureInvariant);
}
