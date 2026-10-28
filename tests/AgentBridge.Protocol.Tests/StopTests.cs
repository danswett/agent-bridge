using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public class StopTests
{
    static StopRequest Stop(string id = "s1", string? confirm = null) => new() { Id = id, Request = "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b", Confirm = confirm };

    [Fact]
    public void A_stop_names_a_session_and_any_confirmation_is_a_token_the_host_could_have_issued()
    {
        Assert.Null(Stops.Check(Stop()));
        Assert.Null(Stops.Check(Stop(confirm: "q2vU3dJ7n0x-AbC_9zYw1A")));
        Assert.Equal("the session id is missing or too long", Stops.Check(Stop(id: "")));
        Assert.Equal("the session id is missing or too long", Stops.Check(Stop(id: new string('x', 257))));
        Assert.Equal("the confirmation is not one this host issued", Stops.Check(Stop(confirm: "")));
        Assert.Equal("the confirmation is not one this host issued", Stops.Check(Stop(confirm: "a b")));
        Assert.Equal("the confirmation is not one this host issued", Stops.Check(Stop(confirm: new string('a', Stops.MaxConfirmLength + 1))));
    }

    [Fact]
    public void A_stop_reads_from_and_writes_to_the_contract_json_and_an_unknown_outcome_is_failed()
    {
        var read = JsonSerializer.Deserialize<StopRequest>("""{"id":"s1","request":"6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b","confirm":"tok","future":1}""", ProtocolJson.Options)!;
        Assert.Equal(("s1", "tok"), (read.Id, read.Confirm));
        Assert.Equal("""{"outcome":"confirm","reason":"this session is still working; the confirmation lapses in 10 s","confirm":"tok","confirmSeconds":10}""",
            JsonSerializer.Serialize(new StopResult(StopOutcome.Confirm, "this session is still working; the confirmation lapses in 10 s") { Confirm = "tok", ConfirmSeconds = 10 }, ProtocolJson.Options));
        Assert.Equal("""{"outcome":"ended","forced":true}""", JsonSerializer.Serialize(new StopResult(StopOutcome.Ended) { Forced = true }, ProtocolJson.Options));
        // Never ended, and never confirm, which would invite a press that confirms nothing.
        Assert.Equal(StopOutcome.Failed, JsonSerializer.Deserialize<StopResult>("""{"outcome":"paused"}""", ProtocolJson.Options)!.Outcome);
    }

    [Fact]
    public void A_session_being_ended_says_ending_and_an_older_reader_takes_it_for_idle()
    {
        Assert.Equal("\"ending\"", JsonSerializer.Serialize(SessionStatus.Ending, ProtocolJson.Options));
        Assert.Equal(SessionStatus.Ending, JsonSerializer.Deserialize<SessionStatus>("\"ending\"", ProtocolJson.Options));
        Assert.Equal(SessionStatus.Idle, JsonSerializer.Deserialize<SessionStatus>("\"resting\"", ProtocolJson.Options));
    }
}
