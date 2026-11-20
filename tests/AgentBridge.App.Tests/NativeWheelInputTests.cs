using Xunit;

namespace AgentBridge.App.Tests;

public sealed class NativeWheelInputTests
{
    [Fact]
    public async Task Settled_child_focus_posts_despite_an_unsuccessful_focus_request()
    {
        var port = new WheelPort { FocusSucceeds = false, SettledFocus = true, Target = 0x4008C };
        using var wheel = new NativeWheelInput(port);
        await wheel.Post();
        Assert.True(port.FocusedWhenPosted);
        Assert.Equal((nint)0x4008C, port.PostedTarget);
    }

    [Fact]
    public async Task Settled_child_focus_with_an_unverified_native_target_still_refuses_the_wheel()
    {
        var port = new WheelPort { FocusSucceeds = false, SettledFocus = true, Verified = false, Target = 0x4008C };
        using var wheel = new NativeWheelInput(port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.Post());
        Assert.DoesNotContain("post", port.Calls);
    }

    [Fact]
    public async Task A_focus_miss_names_every_failed_readiness_condition()
    {
        var port = new WheelPort { LosesFocus = true, Verified = false, Foreground = false };
        using var wheel = new NativeWheelInput(port);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.Post());
        Assert.Contains("failed: list focus, native target, foreground", error.Message);
        Assert.Contains("focus request: True; list focus: False; native target: False; foreground: False", error.Message);
        Assert.DoesNotContain("post", port.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_wheel_keeps_the_list_focusable_until_receipt_and_restores_its_tab_behavior(bool wasTabStop)
    {
        var port = new WheelPort { ListIsTabStop = wasTabStop };
        using (var wheel = new NativeWheelInput(port))
        {
            await wheel.Post();
            Assert.True(port.ListIsTabStop);
            Assert.True(port.FocusedWhenPosted);
            wheel.RecordReceipt(true);
            Assert.True(port.ListIsTabStop);
        }
        Assert.Equal(wasTabStop, port.ListIsTabStop);
    }

    [Fact]
    public async Task Native_wheel_is_posted_only_after_the_list_has_focus_and_the_window_is_foreground()
    {
        var port = new WheelPort();
        using var wheel = new NativeWheelInput(port);
        await wheel.Post();
        Assert.True(port.FocusedWhenPosted);
        Assert.Equal(new[] { "focus", "prepare", "list-focus", "target", "foreground", "post" }, port.Calls);
        Assert.Equal((nint)0x20, port.PostedTarget);
    }

    [Fact]
    public async Task A_view_no_longer_at_the_end_after_focus_refuses_the_wheel()
    {
        var port = new WheelPort { AtEnd = false };
        using var wheel = new NativeWheelInput(port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.Post());
        Assert.DoesNotContain("post", port.Calls);
        Assert.Equal(0, port.PostedTarget);
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    public async Task Unverified_list_focus_target_or_foreground_refuses_the_wheel(
        bool focusSucceeds, bool losesFocus, bool targetVerified, bool foreground)
    {
        var port = new WheelPort
        {
            FocusSucceeds = focusSucceeds, LosesFocus = losesFocus,
            Verified = targetVerified, Foreground = foreground,
        };
        using (var wheel = new NativeWheelInput(port))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.Post());
            Assert.Single(port.Calls, call => call == "focus");
            Assert.DoesNotContain("post", port.Calls);
            Assert.Equal(0, port.PostedTarget);
        }
        Assert.False(port.ListIsTabStop);
    }

    [Fact]
    public async Task Missed_wheel_receipt_logs_the_HWND_chain()
    {
        var port = new WheelPort();
        using var wheel = new NativeWheelInput(port);
        await wheel.Post();
        port.Description = "window: 0xA; target: 0x22; foreground: 0xB";
        port.Chain = [0x22, 0x21, 0xA];
        wheel.RecordReceipt(false);
        var log = Assert.Single(port.Logs);
        Assert.Contains("wheel not received", log);
        Assert.Contains("target: 0x20", log);
        Assert.Contains(port.Description, log);
        Assert.Contains("HWND chain: 0x22 -> 0x21 -> 0xA", log);
        Assert.Contains(log, wheel.Target);
        Assert.Equal(1, port.ChainReads);
    }

    [Fact]
    public async Task A_received_wheel_does_not_read_or_log_a_missed_receipt_chain()
    {
        var port = new WheelPort();
        using var wheel = new NativeWheelInput(port);
        await wheel.Post();
        wheel.RecordReceipt(true);
        Assert.Empty(port.Logs);
        Assert.Equal(0, port.ChainReads);
        Assert.Equal(port.Description, wheel.Target);
    }

    [Fact]
    public async Task The_wheel_waits_for_focus_to_settle_before_checking_the_end_and_posting()
    {
        var focus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new WheelPort { FocusCompletion = focus.Task };
        using var wheel = new NativeWheelInput(port);
        var posting = wheel.Post();
        Assert.False(posting.IsCompleted);
        Assert.DoesNotContain("prepare", port.Calls);
        port.AtEnd = false;
        focus.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => posting);
        Assert.DoesNotContain("post", port.Calls);
    }

    sealed class WheelPort : INativeWheelInputPort
    {
        public readonly List<string> Calls = [], Logs = [];
        public bool FocusSucceeds = true, LosesFocus, Verified = true, Foreground = true;
        public bool Focused, FocusedWhenPosted, AtEnd = true;
        public bool? SettledFocus;
        public bool ListIsTabStop { get; set; }
        public bool ListAtEnd => AtEnd;
        public Task FocusCompletion = Task.CompletedTask;
        public nint PostedTarget;
        public nint Target = 0x20;
        public int ChainReads;
        public string Description = "window: 0xA; target: 0x20; foreground: 0xA";
        public IReadOnlyList<nint> Chain = [0x20, 0xA];
        public async Task<bool> FocusList()
        {
            Calls.Add("focus");
            await FocusCompletion;
            Focused = SettledFocus ?? (FocusSucceeds && ListIsTabStop);
            return FocusSucceeds && ListIsTabStop;
        }
        public bool ListHasFocus { get { Calls.Add("list-focus"); return Focused; } }
        public bool WindowIsForeground { get { Calls.Add("foreground"); return Foreground; } }
        public nint PrepareTarget()
        {
            Calls.Add("prepare");
            if (LosesFocus) { Focused = false; }
            return Target;
        }
        public bool TargetIsVerified(nint target) { Calls.Add("target"); return Verified; }
        public bool Post(nint target)
        {
            Calls.Add("post");
            PostedTarget = target;
            FocusedWhenPosted = Focused;
            return true;
        }
        public string DescribeTarget() => Description;
        public IReadOnlyList<nint> ReadHwndChain() { ChainReads++; return Chain; }
        public void Log(string message) => Logs.Add(message);
    }
}
