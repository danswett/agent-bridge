using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

// docs/protocol/vectors/roster-state.json: the §10 scenarios as entries offered in order, with
// the verdict and refusal code of each and the state the verifier applies after them, so the
// PWA's port of the acceptance rule (R1c) reaches the same verdicts and the same roster. After a
// deliberate change, UPDATE_PROTOCOL_VECTORS=1 writes the file.
public sealed class RosterStateVectorTests
{
    // Offer an entry at a running time, move the clock and record who is served, or adopt a head.
    sealed record Step(string? Offer = null, double? Hours = null, string? Adopt = null);

    sealed record Scenario(string Name, string About, Func<World> Build, Func<World, Step[]>? Steps = null);

    static Step[] All(World w) => [.. w.Labels.Select(l => new Step(l))];

    static IEnumerable<Scenario> Scenarios()
    {
        foreach (var (name, build) in RosterPropertyTests.Builders)
        {
            yield return new(name, "a §10 scenario, every entry offered in order", build);
        }
        yield return new("evidence", "a removal on its target's over-budget 65th sibling add brings it as evidence and applies",
            () => RosterRecoveryTests.Siblings());
        yield return new("self-removal-no-evidence", "a self-removal on an over-budget add is refused for evidence",
            () => RosterRecoveryTests.Siblings(self: true));
        yield return new("active-cap", "the 65th active device's add is refused; a removal is not",
            RosterRecoveryTests.ActiveCap);
        yield return new("window-8", "an add with 8 unseen entries is inside the window", () => RosterStateTests.Window(8));
        yield return new("window-9", "an add with 9 unseen entries is quarantined", () => RosterStateTests.Window(9));
        yield return new("adopting-merge", "quarantine is inherited until an adopting merge adopts the branch",
            () =>
            {
                var w = RosterStateTests.Window(9);
                w.Rename("nY", "H", "+Y", "+Y", "Yonder");
                w.Merge("tidy", "G", ["+D8", "nY"], adopt: true);
                return w;
            });
        yield return new("quarantined-removal", "a removal on a quarantined branch applies when it is valid without it",
            () =>
            {
                var w = RosterStateTests.Window(9);
                w.Remove("G-H", "G", "+H", "+Y");
                return w;
            });
        yield return new("local-adoption", "the person at the machine adopts a quarantined branch",
            () => RosterStateTests.Window(9),
            w => [.. All(w), new Step(Adopt: "+Y")]);
        yield return new("probation", "a probationary device's add is held for 72 hours of running time",
            () =>
            {
                var w = new World();
                w.Genesis();
                w.Add("+A", "G", "A", "G");
                w.Add("+C", "A", "C", "+A");
                return w;
            },
            w => [.. All(w), new Step(Hours: 71), new Step(Hours: 73)]);
        yield return new("held-rename", "a rename by a device on probation, or a held one, is pending until the hold ends",
            () =>
            {
                var w = new World();
                w.Genesis();
                w.Add("+A", "G", "A", "G");
                w.Add("+C", "A", "C", "+A");
                w.Rename("nA", "A", "+A", "+C", "G");
                w.Rename("nC", "C", "+C", "nA", "Kitchen laptop");
                return w;
            },
            w => [.. All(w), new Step(Hours: 0), new Step(Hours: 73)]);
        yield return new("ninth-rename", "the ninth rename of one add is refused",
            () =>
            {
                var w = RosterStateTests.Line();
                var last = "cB";
                for (var i = 0; i < 9; i++) { w.Rename($"n{i}", "G", "+B", last, $"Name {i}"); last = $"n{i}"; }
                return w;
            });
    }

    static string Hash(World w, string label) => Codec.Base64Url(w[label].Hash);

    static JsonObject Snapshot(World w, RosterVerifier v)
    {
        var s = v.State;
        var labels = w.Labels.ToDictionary(l => Codec.Base64Url(w[l].Hash), l => l);
        var active = new JsonObject();
        foreach (var (id, add) in s.Active.OrderBy(p => w.NameOf(p.Key), StringComparer.Ordinal))
        {
            active[w.NameOf(id)] = new JsonObject
            {
                ["add"] = labels[Codec.Base64Url(v.Graph[add].Hash)],
                ["confirmed"] = s.IsConfirmed(add),
                ["name"] = v.Name(id),
            };
            if (v.PendingName(id) is { } pending) { active[w.NameOf(id)]!["pending"] = pending; }
        }
        string[] Where(Func<int, bool> pick) =>
            [.. Enumerable.Range(0, v.Graph.Count).Where(pick).Select(i => labels[Codec.Base64Url(v.Graph[i].Hash)]).Order(StringComparer.Ordinal)];
        return new JsonObject
        {
            ["active"] = active,
            ["conflicted"] = new JsonArray([.. s.Conflicted.Select(w.NameOf).Order(StringComparer.Ordinal).Select(n => (JsonNode)n)]),
            ["heads"] = new JsonArray([.. v.Heads.Select(h => (JsonNode)labels[Codec.Base64Url(h)])]),
            ["quarantined"] = new JsonArray([.. Where(i => v.Holds(v.Graph[i].Hash) && !v.Graph[i].Evidence && v.IsQuarantined(i)).Select(l => (JsonNode)l)]),
            ["evidence"] = new JsonArray([.. Where(i => v.Graph[i].Evidence).Select(l => (JsonNode)l)]),
            ["serving"] = new JsonArray([.. w.Serving(v).Select(n => (JsonNode)n)]),
        };
    }

    static JsonObject Run(Scenario scenario)
    {
        var w = scenario.Build();
        var steps = (scenario.Steps ?? All)(w);
        var v = new RosterVerifier(Account, Generation, () => w.Now);
        var devices = new JsonObject();
        var entries = new JsonArray();
        foreach (var l in w.Labels)
        {
            entries.Add(new JsonObject { ["label"] = l, ["hash"] = Hash(w, l), ["wrapper"] = w[l].Wire });
        }
        var outSteps = new JsonArray();
        foreach (var step in steps)
        {
            var o = new JsonObject();
            if (step.Hours is { } h)
            {
                w.Now = TimeSpan.FromHours(h);
                o["hours"] = h;
            }
            if (step.Offer is { } label)
            {
                var offered = w.Feed(v, label)[0];
                o["offer"] = label;
                o["verdict"] = offered.Verdict.ToString().ToLowerInvariant();
                if (offered.Reason is not null) { o["reason"] = offered.Reason; }
            }
            else if (step.Adopt is { } head)
            {
                v.Adopt(w[head].Hash);
                o["adopt"] = head;
            }
            else
            {
                o["serving"] = new JsonArray([.. w.Serving(v).Select(n => (JsonNode)n)]);
                o["names"] = new JsonObject([.. v.State.Active.Keys.Select(id => KeyValuePair.Create(w.NameOf(id), (JsonNode?)v.Name(id))).OrderBy(p => p.Key, StringComparer.Ordinal)]);
            }
            outSteps.Add(o);
        }
        foreach (var l in w.Labels)
        {
            if (w.Entry(l).Body.Device is { } d) { devices[w.NameOf(d.Id)] = d.Id; }
        }
        return new JsonObject
        {
            ["name"] = scenario.Name,
            ["about"] = scenario.About,
            ["devices"] = devices,
            ["entries"] = entries,
            ["steps"] = outSteps,
            ["state"] = Snapshot(w, v),
        };
    }

    static JsonObject Generate() => new()
    {
        ["about"] = "The device roster's acceptance rule and state (#55 R1b): each scenario's entries offered in order, each step's verdict (accepted, quarantined, duplicate or refused) and refusal code, and the state the verifier then applies. Steps with hours move the verifier's running-time clock first; a step with only hours records who is served then and the names shown; a name is what the verifier shows, with a rename by a device on probation or held left pending; a step with adopt adopts that head locally. Every entry is in entries, by label, whether or not a step offers it.",
        ["spec"] = "devices.md",
        ["generatedBy"] = "tests/AgentBridge.Roster.Tests/RosterStateVectorTests.cs",
        ["account"] = Account,
        ["generation"] = Generation,
        ["scenarios"] = new JsonArray([.. Scenarios().Select(s => (JsonNode)Run(s))]),
    };

    [Fact]
    public void The_committed_roster_state_vectors_are_what_the_library_gives()
    {
        var stale = VectorFiles.Stale(new Dictionary<string, JsonObject> { ["roster-state.json"] = Generate() });
        Assert.True(stale.Count == 0, "docs/protocol/vectors/roster-state.json is not what the roster library gives; with UPDATE_PROTOCOL_VECTORS=1 this test writes it.");
    }

    [Fact]
    public void Every_scenario_ends_where_its_own_test_says()
    {
        var byName = Scenarios().ToDictionary(s => s.Name, Run);
        string[] Active(string name) => [.. byName[name]["state"]!["active"]!.AsObject().Select(p => p.Key)];
        Assert.Equal(["G", "R1", "R2"], Active("reciprocal"));
        Assert.Empty(Active("tie"));
        Assert.Equal(["Y"], byName["local-adoption"]["state"]!["active"]!.AsObject().Where(p => p.Key == "Y").Select(p => p.Key));
        var probation = byName["probation"]["steps"]!.AsArray();
        Assert.Equal(["A", "G"], probation[^2]!["serving"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["A", "C", "G"], probation[^1]!["serving"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("evidence", byName["self-removal-no-evidence"]["steps"]!.AsArray()[^1]!["reason"]!.GetValue<string>());
    }
}
