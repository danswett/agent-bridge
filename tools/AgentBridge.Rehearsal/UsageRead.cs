using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Rehearsal;

internal static class UsageRead
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IClientChannel channel, TextWriter output, CancellationToken ct)
    {
        if (args.Count != 0) { throw new UsageException("usage-read takes no arguments"); }
        try
        {
            var hello = await channel.HelloAsync(ct);
            var report = await channel.ReadAsync<UsageReport>(UsageProtocol.Get, new UsageRequest { Shared = true }, ct: ct);
            var reader = new UsageReader(hello.Machine.Id, hello.Machine.Name);
            var all = report.Agents.Select(a => a.ReadBy is null ? a with { ReadBy = reader } : a).ToList();
            var selected = all.Where((a, i) => Current(a, i, all)).GroupBy(UsageShare.Key).Select(group =>
            {
                var best = group.Aggregate((a, b) => UsageShare.Outranks(b, a) ? b : a);
                var failure = group.Where(a => a.Windows.Count == 0).OrderByDescending(a => a.MeasuredAt).FirstOrDefault();
                return best.Windows.Count > 0 && failure is { Error: { Length: > 0 } why } && failure.MeasuredAt > best.MeasuredAt
                    ? best with { Error = why } : best;
            });
            await output.WriteLineAsync(JsonSerializer.Serialize(new UsageReport([.. selected]), ProtocolJson.Options));
            return 0;
        }
        catch (Exception e) when (e is IOException or HostErrorException or JsonException or TimeoutException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { error = e.Message }, ProtocolJson.Options));
            return 2;
        }
    }

    // Match UsageMerge.Current without a new App.Core project dependency: a reader's newest
    // keyed/keyless kind stands, and an older sign-out cannot erase its newer reading.
    static bool Current(AgentUsage a, int index, List<AgentUsage> all)
    {
        for (var i = 0; i < all.Count; i++)
        {
            var other = all[i];
            if (other.Agent != a.Agent || other.ReadBy?.Id != a.ReadBy?.Id || UsageShare.Keyless(other) == UsageShare.Keyless(a)) { continue; }
            if (a.Windows.Count > 0)
            {
                if (other.Windows.Count > 0 && (other.MeasuredAt > a.MeasuredAt || (other.MeasuredAt == a.MeasuredAt && i < index))) { return false; }
            }
            else
            {
                if (other.Windows.Count > 0 && other.MeasuredAt >= a.MeasuredAt) { return false; }
                if (other.Windows.Count == 0 && (other.MeasuredAt > a.MeasuredAt || (other.MeasuredAt == a.MeasuredAt && i < index))) { return false; }
            }
        }
        return true;
    }
}
