using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// A session's Details rows against the table both clients share (docs/design/session-details.json,
// ui.md §6.2, #1087): the same rows, in the same order, in the same words. The PWA's
// pwa/test/unit/details-rows.test.mjs reads the same file.
[Trait("Category", "Network")]
public sealed class DetailsTableTests : IAsyncLifetime
{
    static readonly JsonElement Table = ReadTable();
    readonly FakeHost host = new();
    readonly string folds = Path.Combine(Path.GetTempPath(), "abf-" + Guid.NewGuid().ToString("N") + ".json");
    HostConnection connection = null!;
    Deliveries deliveries = null!;
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse(Table.GetProperty("now").GetString()!, CultureInfo.InvariantCulture));

    static JsonElement ReadTable()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "design", "session-details.json"))).RootElement.Clone();
    }

    public static TheoryData<int> Scenarios()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Table.GetProperty("scenarios").GetArrayLength(); i++) { data.Add(i); }
        return data;
    }

    public async Task InitializeAsync()
    {
        host.Start();
        connection = host.Connect();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        deliveries = new Deliveries(connection, new LockedDispatcher(), clock);
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
        File.Delete(folds);
    }

    SessionCardViewModel Card(JsonElement scenario)
    {
        var raw = scenario.GetProperty("session");
        var session = AuthState.Attach(JsonSerializer.Deserialize<Session>(raw.GetRawText(), ProtocolJson.Options)!, raw);
        var machine = scenario.GetProperty("machine").GetString();
        DateTimeOffset? awaySince = scenario.TryGetProperty("away", out var away) ? DateTimeOffset.Parse(away.GetProperty("since").GetString()!, CultureInfo.InvariantCulture) : null;
        return new SessionCardViewModel(session, new CardContext
        {
            Deliveries = deliveries,
            Folds = new FoldStore(folds),
            Capabilities = () => connection.Capabilities,
            MachineName = () => machine,
            Host = connection,
            Time = clock,
            Away = now => awaySince is { } since ? StatusRules.Offline(since, now) : null,
        });
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Details_show_the_tables_rows_for_each_session_in_its_order_and_words(int which)
    {
        var scenario = Table.GetProperty("scenarios")[which];
        var order = Table.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("row").GetString()!).ToList();
        var mono = Table.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("row").GetString()!, r => r.TryGetProperty("mono", out var m) && m.GetBoolean());
        var want = scenario.GetProperty("rows").EnumerateArray().ToList();
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(Table.GetProperty("locale").GetString()!);
        try
        {
            var card = Card(scenario);
            card.RefreshTimes();
            var got = card.Details.ToList();

            Assert.Equal(want.Select(w => w.GetProperty("row").GetString()), got.Select(d => d.Label));
            for (var i = 0; i < want.Count; i++)
            {
                var label = want[i].GetProperty("row").GetString()!;
                if (want[i].TryGetProperty("value", out var value)) { Assert.True(value.GetString() == got[i].Value, $"{label}: \"{got[i].Value}\", not \"{value.GetString()}\""); }
                else { Assert.True(Regex.IsMatch(got[i].Value, want[i].GetProperty("match").GetString()!), $"{label}: \"{got[i].Value}\""); }
                Assert.True(mono[label] == got[i].Mono, $"{label} in the mono face");
            }
            var at = want.Select(w => order.IndexOf(w.GetProperty("row").GetString()!)).ToList();
            Assert.True(at.Select((n, i) => n >= 0 && (i == 0 || n > at[i - 1])).All(ok => ok), "the table's own order: " + string.Join(",", at));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Started_has_the_full_timestamp_on_hover_where_its_words_are_a_clock_time()
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var card = Card(Table.GetProperty("scenarios")[0]);
            card.RefreshTimes();
            var started = card.Details.Single(d => d.Label == "Started");
            Assert.NotEqual(started.Value, started.Tip);
            Assert.Contains("2026", started.Tip);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }
}
