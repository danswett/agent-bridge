using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

public sealed class InputObservationTests
{
    static readonly Guid Session = Guid.Parse("1504423a-2053-44c5-951f-bcc8e3809100");
    static readonly DateTimeOffset Now = new(2026, 11, 18, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Occupied", "CopilotScreen", "occupied", "observed")]
    [InlineData("Empty", "CopilotScreen", "empty", "observed")]
    [InlineData("Queued", "QueuedReceipt", "queued", "queuedReceipt")]
    public void Fresh_positive_evidence_keeps_occupied_empty_and_queued_distinct(string evidence, string source, string state, string reason)
    {
        var observation = Observe(evidence, source, ageMs: 25);
        Assert.Equal(state, observation.GetProperty("state").GetString());
        Assert.Equal(reason, observation.GetProperty("reason").GetString());
        Assert.Equal(Session, observation.GetProperty("session").GetGuid());
        Assert.Equal(25, observation.GetProperty("ageMs").GetInt64());
    }

    [Theory]
    [InlineData("Missing", "missing")]
    [InlineData("Ambiguous", "ambiguous")]
    [InlineData("Refused", "refused")]
    [InlineData("Uncertain", "uncertain")]
    [InlineData("Unavailable", "unavailable")]
    [InlineData("777", "uncertain")]
    public void Uncertainty_and_unavailable_never_mean_empty(string evidence, string reason) =>
        Unknown(Observe(evidence), reason);

    [Theory]
    [InlineData("Occupied", "CopilotScreen")]
    [InlineData("Empty", "CopilotScreen")]
    [InlineData("Queued", "QueuedReceipt")]
    public void Stale_evidence_never_preserves_a_positive_state(string evidence, string source) =>
        Unknown(Observe(evidence, source, ageMs: 1001), "stale");

    [Theory]
    [InlineData(null, "missing")]
    [InlineData(-1d, "uncertain")]
    [InlineData(1000.1d, "stale")]
    public void Missing_future_and_fractionally_expired_times_fail_closed(double? ageMs, string reason) =>
        Unknown(Observe("Empty", ageMs: ageMs), reason);

    [Fact]
    public void Evidence_at_the_freshness_boundary_is_still_fresh() =>
        Assert.Equal("empty", Observe("Empty", ageMs: 1000).GetProperty("state").GetString());

    [Theory]
    [InlineData("Queued", "CopilotScreen")]
    [InlineData("Empty", "QueuedReceipt")]
    [InlineData("Occupied", "QueuedReceipt")]
    [InlineData("Empty", "Unknown")]
    [InlineData("Empty", "777")]
    public void A_source_cannot_establish_another_sources_state(string evidence, string source) =>
        Unknown(Observe(evidence, source), "uncertain");

    [Fact]
    public void Missing_session_identity_and_invalid_freshness_policy_fail_closed()
    {
        Unknown(Observe("Empty", session: Guid.Empty), "missing");
        Unknown(Observe("Empty", maxAgeMs: 0), "uncertain");
        Unknown(Observe("Empty", maxAgeMs: -1), "uncertain");
    }

    [Theory]
    [InlineData("Occupied", "CopilotScreen", "copilotScreen")]
    [InlineData("Empty", "CopilotScreen", "copilotScreen")]
    [InlineData("Queued", "QueuedReceipt", "queuedReceipt")]
    [InlineData("Unavailable", "Unknown", "unknown")]
    [InlineData("Empty", "777", "unknown")]
    public void Payloads_expose_only_correlation_age_and_closed_codes_never_input_text(string evidence, string source, string reportedSource)
    {
        var observation = Observe(evidence, source);
        Assert.Equal(new[] { "ageMs", "reason", "session", "source", "state" },
            observation.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(reportedSource, observation.GetProperty("source").GetString());
        Assert.Equal(Session, observation.GetProperty("session").GetGuid());
    }

    static void Unknown(JsonElement observation, string reason)
    {
        Assert.Equal("unknown", observation.GetProperty("state").GetString());
        Assert.Equal(reason, observation.GetProperty("reason").GetString());
    }

    // Reflection keeps the tests-only RED commit compiling before the contract type exists.
    static JsonElement Observe(string evidence, string source = "CopilotScreen", double? ageMs = 0, double maxAgeMs = 1000, Guid? session = null)
    {
        var type = typeof(LocalApi).Assembly.GetType("AgentBridge.Host.InputObservation");
        Assert.NotNull(type);
        var classify = type.GetMethod("Classify");
        Assert.NotNull(classify);
        var parameters = classify.GetParameters();
        var observation = classify.Invoke(null,
        [
            session ?? Session, Enum.Parse(parameters[1].ParameterType, source),
            Enum.Parse(parameters[2].ParameterType, evidence),
            ageMs is { } age ? Now.AddTicks(-(long)(age * TimeSpan.TicksPerMillisecond)) : null,
            Now, TimeSpan.FromMilliseconds(maxAgeMs),
        ]);
        Assert.NotNull(observation);
        return JsonSerializer.SerializeToElement(observation, observation.GetType(), ProtocolJson.Options);
    }
}
