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
    static readonly HashSet<string> CommandReasons = [nameof(Invalid.Reset), nameof(Invalid.Action), nameof(Invalid.Machine), nameof(Invalid.Probation), nameof(Invalid.Lifetime), nameof(Invalid.Future), nameof(Invalid.Expired), nameof(Invalid.Floor), nameof(Invalid.Nonce), nameof(Invalid.Heads), nameof(Invalid.Regress)];

    sealed record Step(string? Offer = null, double? Hours = null, string? Adopt = null);

    sealed record Scenario(string Name, string About, Func<World> Build, Func<World, Step[]>? Steps = null, int? AddCap = null, bool AcceptHostSign = false);

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
        // Quarantine depends on what a verifier held when an entry arrived, so these are in one order:
        // the property tests leave them out.
        yield return new("unrefreshed-removal", "an add on a removal signed 30 entries back is as far outside the window as its parent", () => RosterWindowTests.Unrefreshed(removalFirst: true));
        yield return new("unrefreshed-rename", "an add on a quarantined rename signed 30 entries back is quarantined with it", () => RosterWindowTests.Unrefreshed(removalFirst: false));
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
            w => [.. All(w), new Step(Hours: 71), new Step(Hours: 72 - 1 / 36e9), new Step(Hours: 72), new Step(Hours: 73)]);
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
        // A host's hostSign key (agent-principals.md §8.10, V1) is a verifier's choice to accept, and
        // never another device's key.
        World HostBase()
        {
            var w = new World();
            w.Genesis();
            w.Add("+A", "G", "A", "G");
            return w;
        }
        yield return new("host-sign-off", "a v2 host add is refused while the verifier has not switched v2 on",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A"); return w; });
        yield return new("host-sign", "a v2 host add with its own hostSign is accepted once v2 is on",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A"); return w; }, AcceptHostSign: true);
        yield return new("host-sign-is-a-person-sign", "a hostSign equal to a person's sign key is refused",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A", hostSignOf: "A"); return w; }, AcceptHostSign: true);
        yield return new("host-sign-is-a-person-dh", "a hostSign equal to a person's dh key is refused",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A", hostSignOf: "A", hostSignIsDh: true); return w; }, AcceptHostSign: true);
        yield return new("host-sign-is-another-hosts", "a hostSign equal to another host's hostSign is refused",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A"); w.AddHostV2("+I", "G", "I", "+H", hostSignOf: "H"); return w; }, AcceptHostSign: true);
        yield return new("later-key-is-a-host-sign", "a person's add whose sign key is an active host's hostSign is refused",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A"); w.Add("+P", "G", "P", "+H", signWith: "H"); return w; }, AcceptHostSign: true);
        yield return new("sibling-key-is-a-host-sign", "sibling adds, a host and a person sharing its hostSign as a sign key, are each accepted but cannot be merged",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A"); w.Add("+P", "G", "P", "+A", signWith: "H"); w.Merge("m", "G", ["+H", "+P"]); return w; }, AcceptHostSign: true);
        yield return new("readded-and-host-key", "an add that is both a re-add and shares a hostSign is refused as readded",
            () => { var w = HostBase(); w.AddHostV2("+H", "G", "H", "+A"); w.Add("+A2", "G", "A", "+H", signWith: "H"); return w; }, AcceptHostSign: true);
        yield return new("readded-and-host-key-host-first", "a re-add that shares a hostSign is refused as readded even when the host's add comes before the re-added device's",
            () =>
            {
                var w = new World();
                w.Genesis();
                w.AddHostV2("+H", "G", "H", "G");
                w.Add("+A", "G", "A", "+H");
                w.Add("+A2", "G", "A", "+A", signWith: "H");
                return w;
            }, AcceptHostSign: true);
        yield return new("sibling-person-signs-after-host", "once both siblings have arrived, the person holding a host's hostSign key can sign nothing; the roster's other devices still can",
            () =>
            {
                var w = HostBase();
                w.AddHostV2("+H", "G", "H", "+A");
                w.Add("+P", "G", "P", "+A", signWith: "H");
                w.Rename("nP", "P", "+P", "+P", "Laptop", signAs: "H");
                w.Rename("nA", "G", "+A", "+A", "Desk");
                return w;
            }, AcceptHostSign: true);
        yield return new("sibling-person-signs-after-host-reversed", "the same, with the person's add delivered before the host's",
            () =>
            {
                var w = HostBase();
                w.Add("+P", "G", "P", "+A", signWith: "H");
                w.AddHostV2("+H", "G", "H", "+A");
                w.Rename("nP", "P", "+P", "+P", "Laptop", signAs: "H");
                w.Rename("nA", "G", "+A", "+A", "Desk");
                return w;
            }, AcceptHostSign: true);
        yield return new("ninth-rename", "the ninth rename of one add is refused",
            () =>
            {
                var w = RosterStateTests.Line();
                var last = "cB";
                for (var i = 0; i < 9; i++) { w.Rename($"n{i}", "G", "+B", last, $"Name {i}"); last = $"n{i}"; }
                return w;
            });
        yield return new("refused-account", "an entry of another account is refused", () => Refused(w => w.AccountId = "another-account"));
        yield return new("refused-generation", "an entry of another generation is refused", () => Refused(w => w.GenerationId = new string('f', 32)));
        yield return new("refused-seq", "an entry whose seq is not one past its parents' greatest is refused", () => Refused(w => w.SeqOffset = 1));
        yield return new("refused-genesis", "a second genesis in a generation that has one is refused",
            () =>
            {
                var w = Base();
                w.Genesis("H", "G2");
                return w;
            });
        yield return new("refused-signature", "an add signed by another device's key than its signer's is refused",
            () =>
            {
                var w = Base();
                w.Add("forged", "G", "B", "+A", signAs: "A");
                return w;
            });
        yield return new("refused-presence", "an entry signed by a device whose presence is none is refused",
            () =>
            {
                var w = new World();
                w.Presences["A"] = Presence.None;
                w.Genesis();
                w.Add("+A", "G", "A", "G");
                w.Add("fromA", "A", "B", "+A");
                return w;
            });
        yield return new("refused-readded", "an add of an id the history already added is refused",
            () =>
            {
                var w = Base();
                w.Add("again", "G", "A", "+A");
                return w;
            });
        yield return new("refused-target", "a rename of a device already removed names no active add and is refused",
            () =>
            {
                var w = Base();
                w.Remove("G-A", "G", "+A", "+A");
                w.Rename("late", "G", "+A", "G-A", "Too late");
                return w;
            });
        yield return new("refused-confirm", "a device is confirmed only by its own approver, so another's confirmation is refused",
            () =>
            {
                var w = new World();
                w.Genesis();
                w.Add("+H", "G", "H", "G");
                w.Confirm("cH", "G", "+H", "+H");
                w.Add("+B", "G", "B", "cH");
                w.Confirm("H-cB", "H", "+B", "+B");
                return w;
            });
        yield return new("refused-merge", "a merge of heads one of which is the other's ancestor is refused",
            () =>
            {
                var w = Base();
                w.Merge("m", "G", ["G", "+A"]);
                return w;
            });
        yield return new("refused-cap", "the add past the generation's add cap is refused (the vector's verifier has its add cap at 5)",
            () =>
            {
                var w = new World();
                w.Genesis();
                var last = "G";
                for (var i = 0; i < 6; i++) { w.Add($"+N{i}", "G", $"N{i}", last); last = $"+N{i}"; }
                return w;
            },
            AddCap: 5);
    }

    // A genesis and one add, then an add of B that the rule refuses once `tamper` has bent what it carries.
    static World Refused(Action<World> tamper)
    {
        var w = Base();
        tamper(w);
        w.Add("bent", "G", "B", "+A");
        return w;
    }

    static World Base()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        return w;
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
        var v = new RosterVerifier(Account, Generation, () => w.Now) { AddCap = scenario.AddCap ?? RosterVerifier.MaxAdds, AcceptHostSign = scenario.AcceptHostSign };
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
        var result = new JsonObject
        {
            ["name"] = scenario.Name,
            ["about"] = scenario.About,
            ["devices"] = devices,
            ["entries"] = entries,
            ["steps"] = outSteps,
            ["state"] = Snapshot(w, v),
        };
        if (scenario.AddCap is { } cap) { result.Insert(3, "limits", new JsonObject { ["addCap"] = cap }); }
        if (scenario.AcceptHostSign) { result.Insert(2, "acceptHostSign", true); }
        return result;
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
        Assert.Equal(["A", "G"], probation[^3]!["serving"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["A", "C", "G"], probation[^2]!["serving"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("evidence", byName["self-removal-no-evidence"]["steps"]!.AsArray()[^1]!["reason"]!.GetValue<string>());
        Assert.Equal(["A", "C", "G"], probation[^1]!["serving"]!.AsArray().Select(n => n!.GetValue<string>()));

        // Every refusal reason of the acceptance rule, each shown by a step: one added to the verifier needs a scenario and a place here.
        // The person commands' and vetoes' reasons (R1c) have their own vectors, roster-commands.json.
        string[] reasons = ["parent", "account", "generation", "genesis", "seq", "signer", "signature", "presence", "readded", "target", "seniority", "cosign", "keep", "confirm", "merge", "active-cap", "cap", "budget", "evidence", "host-key", "version"];
        Assert.Equal(reasons.Order(StringComparer.Ordinal), typeof(Invalid).GetFields().Where(f => !CommandReasons.Contains(f.Name)).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal));
        var shown = byName.Values.SelectMany(s => s["steps"]!.AsArray()).Select(s => s!["reason"]?.GetValue<string>()).Where(r => r is not null).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(reasons.Order(StringComparer.Ordinal), shown);
        foreach (var name in byName.Keys.Where(n => n.StartsWith("refused-")))
        {
            Assert.Equal("refused", byName[name]["steps"]!.AsArray()[^1]!["verdict"]!.GetValue<string>());
        }
    }
}
