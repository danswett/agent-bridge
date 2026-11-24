using AgentBridge.Hooks;
using Xunit;

// The Claude Code permission prompt answered from a device (#1003), hook side: what the hook prints for a decision, which decision files it
// accepts, and how long it waits. Sensitive: only a person's decision FOR THIS PROMPT is taken (bound by a one-time nonce, dated no earlier than the
// prompt, consumed on read), nothing is allowed by default, and with no usable decision the hook prints nothing, so Claude shows its own prompt.
// What this does NOT show: that the decision came from a person. The file is unauthenticated (see ClaudeApproval and the design doc).
public sealed class ClaudeApprovalTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    const string Event = """{"session_id":"s-1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf private-folder"}}""";
    const string Nonce = "00112233445566778899aabbccddeeff";
    const string OtherNonce = "ffeeddccbbaa99887766554433221100";
    static readonly string Id = ClaudeApproval.RequestId(Event)!;
    static readonly string Proof = ClaudeApproval.Proof(Nonce, Event)!;

    static string Decision(string? proof = null, string? request = null, string verdict = "deny", string principal = "person", DateTimeOffset? at = null) =>
        "{\"request\":\"" + (request ?? Id) + "\",\"proof\":\"" + (proof ?? Proof) + "\",\"verdict\":\"" + verdict + "\",\"principal\":\"" + principal
        + "\",\"at\":\"" + (at ?? Now).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + "\"}";

    static (string?, string?) Check(string json, DateTimeOffset? now = null, DateTimeOffset? since = null) =>
        ClaudeApproval.Verdict(json, Id, Proof, now ?? Now.AddSeconds(5), since ?? Now);

    [Fact]
    public void A_request_id_is_stable_for_the_same_prompt_differs_for_another_and_holds_none_of_it()
    {
        Assert.Equal(Id, ClaudeApproval.RequestId(Event));
        Assert.NotEqual(Id, ClaudeApproval.RequestId(Event.Replace("private-folder", "other")));
        Assert.Matches("^[0-9a-f]{16}$", Id);
        Assert.Null(ClaudeApproval.RequestId("not json"));
        Assert.Null(ClaudeApproval.RequestId("""{"tool_name":"Bash"}"""));
    }

    [Fact]
    public void The_proof_binds_the_nonce_the_request_the_session_the_tool_and_the_whole_input()
    {
        Assert.Matches("^[0-9a-f]{64}$", Proof);
        Assert.NotEqual(Proof, ClaudeApproval.Proof(OtherNonce, Event));
        Assert.NotEqual(Proof, ClaudeApproval.Proof(Nonce, Event.Replace("s-1", "s-2")));
        Assert.NotEqual(Proof, ClaudeApproval.Proof(Nonce, Event.Replace("Bash", "Write")));
        Assert.NotEqual(Proof, ClaudeApproval.Proof(Nonce, Event.Replace("private-folder", "private-folder2")));
        Assert.Matches("^[0-9a-f]{32}$", ClaudeApproval.NewNonce());
        Assert.NotEqual(ClaudeApproval.NewNonce(), ClaudeApproval.NewNonce());
    }

    [Fact]
    public void The_hook_prints_claudes_decision_for_a_verdict_and_nothing_for_none()
    {
        var deny = ClaudeApproval.Reply("deny");
        Assert.Contains("\"behavior\":\"deny\"", deny);
        Assert.DoesNotContain("private", deny);
        Assert.Equal("", ClaudeApproval.Reply(null));
        Assert.Equal("", ClaudeApproval.Reply("maybe"));
    }

    [Fact]
    public void A_persons_deny_made_for_this_prompt_after_it_began_is_taken() => Assert.Equal(("deny", null), Check(Decision()));

    [Fact]
    public void No_input_can_make_the_hook_print_an_allow_while_there_is_no_authenticated_channel()
    {
        foreach (var verdict in new[] { "allow", "Allow", "allow ", "deny", "", null, "always" }) { Assert.DoesNotContain("allow", ClaudeApproval.Reply(verdict)); }
        // A person's allow, with every other check passed, is refused as unavailable, never taken.
        Assert.Equal((null, "unavailable"), Check(Decision(verdict: "allow")));
        Assert.False(ClaudeApproval.AuthenticatedChannel);
        Assert.False(ClaudeApproval.Enabled(true));
        Assert.False(ClaudeApproval.Enabled(false));
    }

    [Fact]
    public void A_prompt_whose_command_would_be_shown_cut_is_never_answerable()
    {
        Assert.True(ClaudeApproval.Answerable(ClaudeApproval.PreviewMax));
        Assert.False(ClaudeApproval.Answerable(ClaudeApproval.PreviewMax + 1));
    }

    [Fact]
    public void A_decision_made_before_the_prompt_began_is_refused_so_an_old_allow_is_never_reused()
    {
        // The replay: a person's earlier allow, dated 100 s before this prompt, with even the right proof.
        Assert.Equal((null, "early"), Check(Decision(at: Now.AddSeconds(-100)), now: Now.AddSeconds(5)));
        Assert.Equal((null, "early"), Check(Decision(at: Now.AddMilliseconds(-1))));
    }

    [Fact]
    public void A_decision_with_another_prompts_proof_or_none_is_refused_even_if_current_and_from_a_person()
    {
        Assert.Equal((null, "proof"), Check(Decision(proof: ClaudeApproval.Proof(OtherNonce, Event))));
        Assert.Equal((null, "proof"), Check(Decision(proof: "")));
        Assert.Equal((null, "proof"), Check("{\"request\":\"" + Id + "\",\"verdict\":\"allow\",\"principal\":\"person\",\"at\":\"2026-11-24T10:00:01.000Z\"}"));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("")]
    public void An_agents_decision_is_refused_with_its_reason(string principal) => Assert.Equal((null, "principal"), Check(Decision(principal: principal)));

    [Fact]
    public void Another_request_stale_future_unknown_verdict_oversize_or_malformed_is_refused()
    {
        Assert.Equal((null, "request"), Check(Decision(request: "zzz")));
        Assert.Equal((null, "stale"), Check(Decision(), now: Now.AddMinutes(10)));
        Assert.Equal((null, "future"), Check(Decision(at: Now.AddMinutes(5))));
        Assert.Equal((null, "verdict"), Check(Decision(verdict: "always")));
        Assert.Equal((null, "size"), Check(Decision() + new string(' ', ClaudeApproval.MaxDecisionBytes)));
        Assert.Equal((null, "unreadable"), Check("{not json"));
        Assert.Equal((null, "unreadable"), Check(""));
    }

    static Task<string?> Await(Func<string?> read, Action<string> log, TimeSpan? wait = null, DateTimeOffset? start = null)
    {
        var clock = start ?? Now;
        var spent = TimeSpan.Zero;
        return ClaudeApproval.AwaitAsync(read, Id, Proof, Now, () => clock, () => spent, d => { clock += d; spent += d; return Task.CompletedTask; },
            wait ?? TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), log);
    }

    [Fact]
    public async Task The_hook_waits_for_a_decision_and_takes_it_when_it_appears()
    {
        var polls = 0;
        Assert.Equal("deny", await Await(() => ++polls < 3 ? null : Decision(at: Now.AddSeconds(2)), _ => { }));
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task With_no_decision_by_the_deadline_the_hook_gives_none_and_never_allows() => Assert.Null(await Await(() => null, _ => { }));

    [Fact]
    public async Task A_read_that_fails_is_tried_again_and_a_stepped_wall_clock_cannot_stretch_the_wait()
    {
        var polls = 0;
        var log = new List<string>();
        Assert.Equal("deny", await Await(() => ++polls == 1 ? throw new IOException("held open") : Decision(at: Now.AddSeconds(2)), log.Add));
        Assert.Contains("claude approval decision unreadable: read", log);
        // The deadline is the monotonic elapsed time: the wall clock stepped back an hour does not make it wait longer than the 5 polls.
        var polled = 0;
        Assert.Null(await Await(() => { polled++; return null; }, _ => { }, wait: TimeSpan.FromSeconds(5), start: Now.AddHours(-1)));
        Assert.Equal(6, polled);
    }

    [Fact]
    public async Task An_agents_decision_is_logged_by_reason_only_and_ignored_so_the_wait_runs_out()
    {
        var log = new List<string>();
        Assert.Null(await Await(() => Decision(principal: "agent", at: Now.AddSeconds(2)), log.Add, wait: TimeSpan.FromSeconds(10)));
        Assert.Equal(["claude approval decision refused: principal"], log);
    }

    [Fact]
    public void The_hooks_wait_is_shorter_than_the_timeout_claude_is_given_so_the_hook_ends_itself() =>
        Assert.True(ClaudeApproval.Wait < ClaudeApproval.HookTimeout);
}
