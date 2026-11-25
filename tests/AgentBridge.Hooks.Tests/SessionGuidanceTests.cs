using System.Text;
using System.Text.Json;
using AgentBridge.Hooks;
using Xunit;

namespace AgentBridge.Hooks.Tests;

// The session guidance reaches Claude and Codex through the SessionStart hook they already run,
// not through a block in the person's own instruction file. In-process streams and a temp folder
// only: no host, no agent home.
[Collection(nameof(HookRunnerTests))]
public sealed class SessionGuidanceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ab-guidance-" + Guid.NewGuid().ToString("N")[..8]);
    readonly DateTimeOffset _now = new(2026, 11, 24, 14, 0, 0, TimeSpan.FromHours(-8));

    string Bin => Path.Combine(_root, "bin");
    string Runtime => Path.Combine(_root, "runtime");

    public SessionGuidanceTests()
    {
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Runtime);
        File.WriteAllBytes(Path.Combine(Bin, HookRunner.PointerFile), HookRunner.Pointer(Runtime));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    void Heartbeat(bool alive)
    {
        if (!alive) { return; }
        var path = SpoolContract.HeartbeatPath(Runtime);
        File.WriteAllText(path, "1");
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime);
    }

    string Invoke(string stdin, params string[] args)
    {
        var env = new HookEnvironment
        {
            ExecutableDirectory = Bin, Now = () => _now, ProcessId = 4242, Ancestors = () => [20, 30], ElapsedMs = () => 7,
        };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        Assert.Equal(0, HookRunner.Run(args, input, output, env));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    static string Event(string name) => "{\"session_id\":\"s1\",\"hook_event_name\":\"" + name + "\"}";

    static string ContextOf(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        var output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", output.GetProperty("hookEventName").GetString());
        return output.GetProperty("additionalContext").GetString()!;
    }

    [Theory]
    [InlineData("claude", "register", true)]
    [InlineData("claude", "register", false)]
    [InlineData("codex", "hook", true)]
    [InlineData("codex", "hook", false)]
    public void A_Claude_or_Codex_session_start_is_given_the_guidance_with_the_host_up_or_down(string agent, string hook, bool hostUp)
    {
        Heartbeat(hostUp);
        var reply = Invoke(Event("SessionStart"), agent, hook);
        Assert.EndsWith("\n", reply);
        Assert.Equal(SessionGuidance.Text.ReplaceLineEndings("\n"), ContextOf(reply));
    }

    [Theory]
    [InlineData("claude", "register", "UserPromptSubmit")]
    [InlineData("claude", "stop", "Stop")]
    [InlineData("claude", "ask", "PreToolUse")]
    [InlineData("claude", "notification", "Notification")]
    [InlineData("codex", "hook", "UserPromptSubmit")]
    [InlineData("codex", "hook", "PreToolUse")]
    [InlineData("codex", "hook", "PermissionRequest")]
    [InlineData("codex", "hook", "Stop")]
    [InlineData("codex", "hook", "SessionEnd")]
    public void Every_other_event_still_prints_nothing_so_no_prompt_or_decision_is_changed(string agent, string hook, string hookEvent)
    {
        Heartbeat(true);
        Assert.Equal("", Invoke(Event(hookEvent), agent, hook));
    }

    [Theory]
    [InlineData("claude", "stop")]
    [InlineData("claude", "ask")]
    [InlineData("claude", "notification")]
    public void Only_the_register_hook_carries_the_guidance_even_for_a_session_start_event(string agent, string hook)
    {
        Heartbeat(true);
        Assert.Equal("", Invoke(Event("SessionStart"), agent, hook));
    }

    [Theory]
    [InlineData("copilot", "agent_stop", "{}\n")]
    [InlineData("copilot", "permission", "{}\n")]
    public void Copilot_replies_are_unchanged_for_a_session_start_event(string agent, string hook, string want)
    {
        Heartbeat(true);
        Assert.Equal(want, Invoke(Event("SessionStart"), agent, hook));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"session_id\":\"s1\"}")]
    [InlineData("{\"hook_event_name\":7}")]
    public void An_event_with_no_readable_name_prints_nothing_and_never_fails_the_agent(string stdin)
    {
        Heartbeat(false);
        Assert.Equal("", Invoke(stdin, "claude", "register"));
        Assert.Equal("", Invoke(stdin, "codex", "hook"));
    }

    [Fact]
    public void The_fallback_reply_stays_fixed_because_it_is_printed_before_the_event_is_read()
    {
        Assert.Equal("", Invoke(Event("SessionStart"), "--fallback", "claude", "register"));
        Assert.Equal("", Invoke(Event("SessionStart"), "--fallback", "codex", "hook"));
    }

    [Fact]
    public void A_session_start_is_still_spooled_to_the_host_beside_the_guidance()
    {
        Heartbeat(true);
        Invoke(Event("SessionStart"), "claude", "register");
        Assert.Single(Directory.GetFiles(SpoolContract.SpoolDirectory(Runtime)));
    }

    [Fact]
    public void The_guidance_names_who_the_user_is_and_forbids_forging_approval()
    {
        // The text is the owner-approved wording from #674 moved unchanged; this pins its anchors
        // so a later edit cannot quietly drop one of them.
        Assert.StartsWith("# Agent Bridge session guidance", SessionGuidance.Text);
        Assert.Contains("agent@session principal, never the user", SessionGuidance.Text);
        Assert.Contains("Never forge an approval", SessionGuidance.Text);
        Assert.Contains("Never sweep or recursively delete the worktree", SessionGuidance.Text);
        Assert.Contains("Hook installation alone is not proof of readiness", SessionGuidance.Text);
    }
}
