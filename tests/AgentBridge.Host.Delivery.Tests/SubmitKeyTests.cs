using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using Xunit;

public sealed class SubmitKeyTests
{
    [Fact]
    public void Compat_false_does_not_discard_Queue_and_explicit_None_removes_it()
    {
        var send = new KeySend("hello", SubmitKey.Queue, 0, 0);
        Assert.Equal(SubmitKey.Queue, (send with { Enter = false }).Submit);
        Assert.Equal(SubmitKey.None, (send with { Submit = SubmitKey.None }).Submit);
    }

    [Fact]
    public void Queue_is_a_distinct_submit_with_its_own_delay_and_never_Enter()
    {
        var plan = new KeyPlan([new("hello", SubmitKey.Queue, 300, 20)]);
        Assert.Contains(" 2 300 20\n", plan.ToWorkerScript());
        Assert.False(plan.Sends[0].Enter);
        Assert.Equal(TimeSpan.FromMilliseconds(320), plan.Duration);
        Assert.Equal("5 chars queue", plan.Describe());
    }

    [Theory]
    [InlineData("0", SubmitKey.None)]
    [InlineData("1", SubmitKey.Enter)]
    [InlineData("2", SubmitKey.Queue)]
    public void Worker_decodes_every_explicit_submit_without_treating_queue_as_none(string token, SubmitKey expected)
    {
        Assert.True(SubmitKeys.TryDecode(token, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("01")]
    [InlineData("queue")]
    public void Unknown_worker_submit_is_refused_before_any_keys(string token) =>
        Assert.False(SubmitKeys.TryDecode(token, out _));

    [Fact]
    public void Undefined_submit_never_becomes_an_unsubmitted_text_burst()
    {
        Assert.Throws<ArgumentException>(() => new KeyPlan([new("hello", (SubmitKey)99, 0, 0)]).ToWorkerScript());
        Assert.Throws<ArgumentException>(() => ConsoleKeyInput.Submit((SubmitKey)99));
    }

    [Fact]
    public void Queue_writes_Q_down_and_up_with_left_control_not_a_text_control_character()
    {
        var records = ConsoleKeyInput.Submit(SubmitKey.Queue);
        Assert.Equal(2, records.Length);
        Assert.Equal(new[] { 1, 0 }, records.Select(r => r.KeyDown));
        Assert.All(records, r =>
        {
            Assert.Equal((ushort)1, r.EventType);
            Assert.Equal((ushort)1, r.RepeatCount);
            Assert.Equal((ushort)0x51, r.VirtualKeyCode);
            Assert.Equal(8u, r.ControlKeyState);
            Assert.Equal('\u0011', r.UnicodeChar);
        });
        Assert.Empty(ConsoleKeyInput.Submit(SubmitKey.None));
    }

    [Fact]
    public void Text_keeps_UTF16_pairs_and_Enter_retains_its_virtual_key()
    {
        var records = ConsoleKeyInput.Text("\uD83D\uDE80\r");
        Assert.Equal(6, records.Length);
        Assert.Equal(new[] { '\uD83D', '\uDE80', '\r' }, records.Where(r => r.KeyDown == 1).Select(r => r.UnicodeChar));
        Assert.All(records[..4], r => Assert.Equal((ushort)0, r.VirtualKeyCode));
        Assert.All(records[4..], r => Assert.Equal((ushort)13, r.VirtualKeyCode));
        Assert.All(ConsoleKeyInput.Submit(SubmitKey.Enter), r => Assert.Equal((ushort)13, r.VirtualKeyCode));
    }

    [Fact]
    public void Every_unqualified_terminal_waits_while_working_then_submits_only_when_idle()
    {
        foreach (var kind in Enum.GetValues<DeliveryTerminalKind>().Append((DeliveryTerminalKind)99))
        {
            Assert.Null(QueueSubmitPolicy.Select(kind, false, true));
            Assert.Equal(SubmitKey.Enter, QueueSubmitPolicy.Select(kind, true, true));
            Assert.Equal(SubmitKey.Enter, QueueSubmitPolicy.Select(kind, false, true, urgent: true));
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Unknown_state_or_occupied_box_never_authorizes_even_urgent_keys(bool? idle, bool? empty)
    {
        Assert.Null(QueueSubmitPolicy.Select(DeliveryTerminalKind.WindowsTerminal, idle, empty));
        Assert.Null(QueueSubmitPolicy.Select(DeliveryTerminalKind.WindowsTerminal, idle, empty, urgent: true));
    }
}
