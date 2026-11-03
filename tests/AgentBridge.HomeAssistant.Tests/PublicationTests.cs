using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

public class PublicationTests
{
    const string Dashboard = "agent-decisions";
    static readonly JsonElement Fixture = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication.json"))).RootElement.Clone();

    public static IEnumerable<object[]> ValidationCases() =>
        Fixture.GetProperty("validations").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()!, c });

    public static IEnumerable<object[]> ProgressCases() =>
        Fixture.GetProperty("progress").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()!, c });

    public static IEnumerable<object[]> WriterCases() =>
        Fixture.GetProperty("writers").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()!, c });

    [Fact]
    public void The_policy_resource_and_digest_match_the_legacy_writer_byte_for_byte()
    {
        var policy = HaPublicationPolicy.Read(Fixture.GetProperty("policy"), Dashboard);
        Assert.Equal(Fixture.GetProperty("canonical").GetString(), policy.CanonicalJson);
        Assert.Equal(Fixture.GetProperty("hash").GetString(), policy.Hash);
        Assert.Equal(Fixture.GetProperty("resourceUrl").GetString(), policy.ResourceUrl);
        Assert.Equal(policy.CanonicalJson, HaPublicationPolicy.ReadResource(policy.ResourceUrl, Dashboard).CanonicalJson);
    }

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public void Policy_validation_agrees_with_the_legacy_writer(string name, JsonElement example)
    {
        var error = Record.Exception(() => HaPublicationPolicy.Read(example.GetProperty("policy"), Dashboard));
        Assert.True(example.GetProperty("accepted").GetBoolean() == (error is null), $"{name}: {error}");
        if (error is not null) { Assert.IsType<InvalidDataException>(error); }
    }

    [Theory]
    [MemberData(nameof(ProgressCases))]
    public void Observing_a_policy_preserves_the_legacy_high_water_and_rollback_fences(string name, JsonElement example)
    {
        var previous = HaPublicationPolicy.Read(example.GetProperty("previous"), Dashboard);
        var current = HaPublicationPolicy.Read(example.GetProperty("current"), Dashboard);
        var error = Record.Exception(() => current.CheckProgressFrom(previous));
        Assert.True(example.GetProperty("accepted").GetBoolean() == (error is null), $"{name}: {error}");
        if (error is not null) { Assert.IsType<InvalidDataException>(error); }
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public void Writer_authorization_and_actual_pinned_card_checks_agree_with_the_legacy_writer(string name, JsonElement example)
    {
        var policy = HaPublicationPolicy.Read(example.GetProperty("policy"), Dashboard);
        var settings = example.GetProperty("settings");
        var artifact = HaPublicationArtifact.Read(example.GetProperty("artifact"));
        var component = example.GetProperty("component").GetString() == "card" ? HaPublicationComponent.Card : HaPublicationComponent.Render;
        var error = Record.Exception(() => policy.CheckWriter(
            settings.GetProperty("authority").GetString()!, settings.GetProperty("participant").GetString()!,
            settings.GetProperty("writer").GetString()!, settings.GetProperty("generation").GetInt32(),
            component, artifact, example.GetProperty("cardUrl").GetString()!));
        if (name is "exact" or "newer" or "pin-exact" or "card-repair")
        {
            Assert.True(example.GetProperty("accepted").GetBoolean(), $"{name}: the recorded legacy writer must authorize this publication");
        }
        Assert.True(example.GetProperty("accepted").GetBoolean() == (error is null), $"{name}: {error}");
        if (error is not null) { Assert.IsType<InvalidDataException>(error); }
    }

    [Theory]
    [InlineData("protocol", "\"1\"")]
    [InlineData("protocol", "null")]
    [InlineData("generation", "\"3\"")]
    [InlineData("generation", "true")]
    [InlineData("generation", "2147483648")]
    [InlineData("generation", "3.5")]
    public void Wrong_numeric_types_are_a_refusal_not_a_parser_crash(string member, string value)
    {
        var policy = JsonNode.Parse(Fixture.GetProperty("canonical").GetString()!)!.AsObject();
        policy[member] = JsonNode.Parse(value);
        using var document = JsonDocument.Parse(policy.ToJsonString());
        Assert.Throws<InvalidDataException>(() => HaPublicationPolicy.Read(document.RootElement, Dashboard));
    }

    [Fact]
    public void Duplicate_policy_or_artifact_members_are_not_silently_overwritten()
    {
        var canonical = Fixture.GetProperty("canonical").GetString()!;
        foreach (var json in new[]
        {
            canonical.Replace("\"generation\":3", "\"generation\":2,\"generation\":3", StringComparison.Ordinal),
            canonical.Replace("\"version\":\"1.0.0\"", "\"version\":\"0.9.0\",\"version\":\"1.0.0\"", StringComparison.Ordinal),
        })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<InvalidDataException>(() => HaPublicationPolicy.Read(document.RootElement, Dashboard));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/local/agent-bridge-publication-policy.js")]
    [InlineData("data:text/javascript;base64,ZXhwb3J0IHt9Ow==#agent-bridge-publication-policy.js?policy=not-base64")]
    public void An_unreadable_resource_is_not_permission_to_bootstrap(string url)
    {
        Assert.Throws<InvalidDataException>(() => HaPublicationPolicy.ReadResource(url, Dashboard));
    }

    [Fact]
    public void Oversized_or_invalid_utf8_resources_are_refused_without_decoding_them_as_replacement_text()
    {
        Assert.Throws<InvalidDataException>(() => HaPublicationPolicy.ReadResource(
            HaPublicationPolicy.ResourcePrefix + new string('A', 16385), Dashboard));
        Assert.Throws<InvalidDataException>(() => HaPublicationPolicy.ReadResource(
            HaPublicationPolicy.ResourcePrefix + Convert.ToBase64String([0xff]), Dashboard));
    }

    [Fact]
    public void A_pinned_cards_body_must_agree_with_its_declared_version()
    {
        var url = "data:text/javascript;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("const CARD_VERSION = '1.0.0';")) +
            "#agent-bridge-reply-card.js?v=2.0.0";
        Assert.Throws<InvalidDataException>(() => HaPublicationArtifact.ReadInlineCard(url));
    }
}
