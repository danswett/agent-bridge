using System.Net;
using System.Text.Json;

internal sealed record LaunchEvidenceRow(string Machine, string Agent, string Model, string Workspace,
    string Outcome, int Starts, string Answered, string? Reason);

// Test-only evidence for fleet #210. Production wording is separate: agent-bridge #1841.
internal static class LaunchRefusalEvidence
{
    public static string Render(IReadOnlyList<LaunchEvidenceRow> rows)
    {
        using var output = new StringWriter { NewLine = "\n" };
        output.WriteLine(LaunchOptionsSyntheticFixture.Provenance);
        output.WriteLine("Starts are observed at the fake process starter; answers are not probed.");
        output.WriteLine();
        output.WriteLine("| Machine | Agent | Model | Workspace | Outcome | Started | Answered | Raw refusal |");
        output.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            output.WriteLine($"| {Cell(row.Machine)} | {Cell(row.Agent)} | {Cell(row.Model)} | {Cell(row.Workspace)} | {Cell(row.Outcome)} | {(row.Starts == 0 ? "no" : "yes")} | {Cell(row.Answered)} | {Cell(row.Reason ?? "")} |");
        }
        // The table escapes delimiters; the JSON retains the exact refusal for replay and audit.
        output.WriteLine();
        output.WriteLine("```json");
        output.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine("```");
        return output.ToString();
    }

    static string Cell(string value) => WebUtility.HtmlEncode(value).Replace("|", "&#124;")
        .Replace("`", "&#96;").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");
}
