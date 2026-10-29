using System.Text.Json;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class HaCheckTests
{
    static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    static HaExpectations Expect(string? slug = "rehearsal_m", bool online = false, bool offline = false, int? sessions = null, bool noDuplicates = false,
        string[]? entities = null, string[]? noEntities = null, Dictionary<string, string>? states = null) =>
        new(slug, online, offline, sessions, noDuplicates, entities ?? [], noEntities ?? [], states ?? []);

    [Theory]
    [InlineData("http://127.0.0.1:8123", true)]
    [InlineData("http://localhost:8123", true)]
    [InlineData("http://[::1]:8123", true)]
    [InlineData("http://homeassistant.local:8123", false)]
    [InlineData("http://192.168.1.10:8123", false)]
    [InlineData("https://example.ui.nabu.casa", false)]
    public void Anything_but_this_machine_is_refused_before_a_request_is_made(string url, bool allowed) =>
        Assert.Equal(allowed, HaCheck.Refusal(new Uri(url)) is null);

    [Fact]
    public void A_second_registration_of_an_entity_is_a_duplicate_but_a_further_Copilot_account_is_not()
    {
        var duplicates = HaCheck.Duplicates([
            "binary_sensor.agent_bridge_rehearsal_m_online",
            "binary_sensor.agent_bridge_rehearsal_m_online_2",
            "sensor.agent_bridge_rehearsal_m_usage_copilot",
            "sensor.agent_bridge_rehearsal_m_usage_copilot_2",
            "sensor.agent_bridge_pc_2_sessions",
            "sensor.kitchen_temperature_2",
        ]);
        Assert.Equal(["binary_sensor.agent_bridge_rehearsal_m_online_2"], duplicates);
    }

    [Fact]
    public void The_machines_online_state_and_session_count_are_read_from_its_own_entities()
    {
        var states = Json(new object[]
        {
            new { entity_id = "binary_sensor.agent_bridge_rehearsal_m_online", state = "on" },
            new { entity_id = "sensor.agent_bridge_rehearsal_m_sessions", state = "1" },
            new { entity_id = "binary_sensor.agent_bridge_rehearsal_w_online", state = "off" },
        });
        Assert.Equal(Verdict.Pass, HaCheck.Evaluate(states, default, Expect(online: true, sessions: 1)).Verdict);
        var wrong = HaCheck.Evaluate(states, default, Expect(online: true, sessions: 0));
        Assert.Equal(Verdict.Fail, wrong.Verdict);
        Assert.Contains("sensor.agent_bridge_rehearsal_m_sessions is 1, expected 0", wrong.Detail, StringComparison.Ordinal);
        Assert.Equal(Verdict.Pass, HaCheck.Evaluate(states, default, Expect(slug: "rehearsal_w", offline: true)).Verdict);
    }

    [Fact]
    public void An_entity_that_should_have_gone_fails_while_it_is_still_registered()
    {
        var states = Json(Array.Empty<object>());
        var registry = Json(new object[] { new { entity_id = "sensor.agent_bridge_abc_status" } });
        var outcome = HaCheck.Evaluate(states, registry, Expect(noEntities: ["sensor.agent_bridge_abc_status"]));
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal(Verdict.Pass, HaCheck.Evaluate(states, Json(Array.Empty<object>()), Expect(noEntities: ["sensor.agent_bridge_abc_status"])).Verdict);
    }

    [Fact]
    public async Task Home_Assistant_that_cannot_be_reached_yet_is_asked_again_until_the_deadline()
    {
        var calls = 0;
        var recovered = await Poll.UntilAsync("Home Assistant", _ => ++calls < 3 ? throw new HttpRequestException("connection refused") : Task.FromResult(Outcome.Pass("ok")),
            DateTimeOffset.UtcNow.AddSeconds(10), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.Equal(Verdict.Pass, recovered.Verdict);
        Assert.Equal(3, calls);

        var never = await Poll.UntilAsync("Home Assistant", _ => throw new HttpRequestException("connection refused"),
            DateTimeOffset.UtcNow.AddMilliseconds(100), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.Equal(Verdict.Error, never.Verdict);
        Assert.Contains("connection refused", never.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Checking_a_machine_without_its_slug_is_a_usage_error()
    {
        Assert.Throws<UsageException>(() => HaCheck.Evaluate(Json(Array.Empty<object>()), default, Expect(slug: null, online: true)));
    }
}
