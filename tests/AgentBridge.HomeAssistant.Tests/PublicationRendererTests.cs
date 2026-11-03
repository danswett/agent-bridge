using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using AgentBridge.HomeAssistant;
using Xunit;

public sealed class PublicationRendererTests
{
    static readonly JsonElement Fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-renderer.json"))).RootElement.Clone();
    public static IEnumerable<object[]> Cases() => Fixture.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("version").GetString()!, c.GetProperty("scenario").GetString()!, c.Clone() });

    [Theory, MemberData(nameof(Cases))]
    public void Full_native_view_matches_the_recorded_legacy_renderer_at_each_feature_gate(string version, string scenario, JsonElement example)
    {
        using var culture = new RendererCulture(example.GetProperty("culture").GetString()!);
        var sessions = example.GetProperty("sessions").Deserialize<HaFleetSession[]>()!;
        var machines = example.GetProperty("machines").Deserialize<HaFleetMachine[]>()!;
        var actual = HaPublicationRenderer.Render(sessions, machines, example.GetProperty("selector").GetString()!,
            example.GetProperty("cardUrl").GetString()!, example.GetProperty("trusted").GetBoolean());
        var expected = JsonNode.Parse(example.GetProperty("config").GetRawText())!.AsObject();
        var receipt = expected["agent_bridge_publication"]!.AsObject();
        var policyJson = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication.json")))!["policy"]!;
        policyJson["render"] = receipt["render"]!.DeepClone();
        policyJson["highRender"] = receipt["render"]!.DeepClone();
        var policy = HaPublicationPolicy.Read(JsonSerializer.SerializeToElement(policyJson), "agent-decisions");
        var proof = HaVerifiedPublication.Read(new(HaPublicationStateKind.Established, policy, null,
            new("card", example.GetProperty("cardUrl").GetString()!), default, true, new(true, expected.ToJsonString())));
        Assert.True(proof.Verified, proof.Reason);
        Assert.Equal(example.GetProperty("inputJson").GetString(), HaPublicationRenderer.InputJson(sessions, machines,
            example.GetProperty("selector").GetString()!, example.GetProperty("cardUrl").GetString()!, example.GetProperty("trusted").GetBoolean()));
        Assert.Equal(receipt["inputHash"]!.GetValue<string>(), actual.InputHash);
        expected.Remove("agent_bridge_publication");
        Assert.True(JsonNode.DeepEquals(expected, actual.Config), $"Full view differs for card {version}/{scenario}");
    }

    sealed class RendererCulture : IDisposable
    {
        readonly CultureInfo previous = CultureInfo.CurrentCulture;
        public RendererCulture(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }

    [Fact]
    public void Bundled_card_is_exactly_versioned_and_renderer_outputs_are_stable_across_fleet_order()
    {
        Assert.Equal("1.42.0", HaPublicationRenderer.Card.VersionText);
        Assert.Equal(Fixture.GetProperty("source").GetProperty("cardHash").GetString(), HaPublicationRenderer.Card.Hash);
        var sessions = new[] { new HaFleetSession("agent_bridge_two", "Two", "Beta"), new HaFleetSession("agent_bridge_one", "One", "Alpha") };
        var machines = new[] { new HaFleetMachine("beta", "Beta"), new HaFleetMachine("alpha", "Alpha") };
        var first = HaPublicationRenderer.Render(sessions, machines, "", HaPublicationRenderer.CardUrl);
        var reversed = HaPublicationRenderer.Render(sessions.Reverse().ToArray(), machines.Reverse().ToArray(), "", HaPublicationRenderer.CardUrl);
        Assert.Equal(first.InputHash, reversed.InputHash);
        Assert.Equal(first.Config.ToJsonString(), reversed.Config.ToJsonString());
        Assert.True(HaPublicationRenderer.TrustCard(HaPublicationRenderer.CardUrl));
        Assert.False(HaPublicationRenderer.TrustCard(HaPublicationRenderer.CardUrl.Replace("?v=1.42.0", "?v=1.43.0", StringComparison.Ordinal)));
        Assert.True(HaPublicationRenderer.TrustCard(""));
        Assert.Equal(64, HaPublicationRenderer.Artifact.Hash.Length);
    }

    [Fact]
    public void Template_values_are_substituted_once_and_unsafe_service_identifiers_are_refused()
    {
        var rendered = HaPublicationRenderer.Render([new("agent_bridge_one", "__BRIDGE_MACHINE_NAME__", "Other")], [new("alpha", "Alpha")], "", HaPublicationRenderer.CardUrl);
        Assert.Contains("__BRIDGE_MACHINE_NAME__", rendered.Config.ToJsonString(), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => HaPublicationRenderer.Render([], [new("a/b", "Unsafe")], "", ""));
        Assert.Throws<InvalidDataException>(() => HaPublicationRenderer.Render([new("agent_bridge_one,all", "Unsafe", "Alpha")], [new("alpha", "Alpha")], "", ""));
        Assert.Throws<InvalidDataException>(() => HaPublicationRenderer.Render([], [new("alpha", "Alpha")], "button.all", ""));
    }
}
