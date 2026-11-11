using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Rehearsal;

internal static class ClientConformanceCommand
{
    static readonly JsonSerializerOptions InputJson = new(Evidence.Json)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static int Run(Parsed p, TextWriter output)
    {
        var runs = ReadArray<ConformanceRun>(p.Option("--runs")!);
        var capabilities = ReadArray<ConformanceCapability>(p.Option("--capabilities")!);
        var client = p.Option("--client")!;
        if (!runs.Any(run => run.Client == client))
            throw new UsageException($"No required run for client '{client}'.");
        var results = File.ReadLines(p.Option("--results")!).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(Read<ConformanceResult>).ToArray();
        var rows = ClientConformance.Build(runs, capabilities, results).Where(row => row.Run.Client == client).ToArray();
        foreach (var line in ClientConformance.JsonLines(rows))
            output.WriteLine(line);
        return rows.All(row => row.Verdict == ConformanceVerdict.Pass) ? 0 : 1;
    }

    static T Read<T>(string json) where T : class
        => JsonSerializer.Deserialize<T>(json, InputJson) ?? throw new JsonException("Conformance input must not be null.");

    static T[] ReadArray<T>(string path) where T : class
    {
        var items = Read<T[]>(File.ReadAllText(path));
        if (items.Any(item => item is null))
            throw new JsonException("Conformance input must not contain null entries.");
        return items;
    }
}
