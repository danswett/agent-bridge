using Xunit;

namespace AgentBridge.App.Tests;

public sealed class NativeWheelInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_wheel_keeps_the_list_focusable_until_receipt_and_restores_its_tab_behavior(bool wasTabStop)
    {
        var port = new WheelPort { ListIsTabStop = wasTabStop };
        using (var wheel = new NativeWheelInput(port))
        {
            wheel.Post();
            Assert.True(port.ListIsTabStop);
            Assert.True(port.FocusedWhenPosted);
            wheel.RecordReceipt(true);
            Assert.True(port.ListIsTabStop);
        }
        Assert.Equal(wasTabStop, port.ListIsTabStop);
    }

    [Fact]
    public void Native_wheel_is_posted_only_after_the_list_has_focus_and_the_window_is_foreground()
    {
        var port = new WheelPort();
        using var wheel = new NativeWheelInput(port);
        wheel.Post();
        Assert.True(port.FocusedWhenPosted);
        Assert.Equal(new[] { "focus", "prepare", "list-focus", "target", "foreground", "post" }, port.Calls);
        Assert.Equal((nint)0x20, port.PostedTarget);
    }

    [Fact]
    public void A_view_no_longer_at_the_end_after_focus_refuses_the_wheel()
    {
        var port = new WheelPort { AtEnd = false };
        using var wheel = new NativeWheelInput(port);
        Assert.Throws<InvalidOperationException>(() => wheel.Post());
        Assert.DoesNotContain("post", port.Calls);
        Assert.Equal(0, port.PostedTarget);
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    public void Unverified_list_focus_target_or_foreground_refuses_the_wheel(
        bool focusSucceeds, bool losesFocus, bool targetVerified, bool foreground)
    {
        var port = new WheelPort
        {
            FocusSucceeds = focusSucceeds, LosesFocus = losesFocus,
            Verified = targetVerified, Foreground = foreground,
        };
        using (var wheel = new NativeWheelInput(port))
        {
            Assert.Throws<InvalidOperationException>(() => wheel.Post());
            Assert.Single(port.Calls, call => call == "focus");
            Assert.DoesNotContain("post", port.Calls);
            Assert.Equal(0, port.PostedTarget);
        }
        Assert.False(port.ListIsTabStop);
    }

    [Fact]
    public void Missed_wheel_receipt_logs_the_HWND_chain()
    {
        var port = new WheelPort();
        using var wheel = new NativeWheelInput(port);
        wheel.Post();
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
    public void A_received_wheel_does_not_read_or_log_a_missed_receipt_chain()
    {
        var port = new WheelPort();
        using var wheel = new NativeWheelInput(port);
        wheel.Post();
        wheel.RecordReceipt(true);
        Assert.Empty(port.Logs);
        Assert.Equal(0, port.ChainReads);
        Assert.Equal(port.Description, wheel.Target);
    }

    sealed class WheelPort : INativeWheelInputPort
    {
        public readonly List<string> Calls = [], Logs = [];
        public bool FocusSucceeds = true, LosesFocus, Verified = true, Foreground = true;
        public bool Focused, FocusedWhenPosted, AtEnd = true;
        public bool ListIsTabStop { get; set; }
        public bool ListAtEnd => AtEnd;
        public nint PostedTarget;
        public int ChainReads;
        public string Description = "window: 0xA; target: 0x20; foreground: 0xA";
        public IReadOnlyList<nint> Chain = [0x20, 0xA];
        public bool FocusList() { Calls.Add("focus"); return Focused = FocusSucceeds && ListIsTabStop; }
        public bool ListHasFocus { get { Calls.Add("list-focus"); return Focused; } }
        public bool WindowIsForeground { get { Calls.Add("foreground"); return Foreground; } }
        public nint PrepareTarget()
        {
            Calls.Add("prepare");
            if (LosesFocus) { Focused = false; }
            return 0x20;
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
