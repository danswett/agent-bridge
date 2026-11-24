using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Windows;
using Xunit;

public sealed class LaunchConsoleSizeTests
{
    [Fact]
    public void A_narrow_launch_grows_the_buffer_before_requesting_a_modest_window()
    {
        var console = new FakeConsole(new(70, 25, 0, 0, 69, 24));
        Assert.True(ConsoleLaunchSize.Apply(console, 100));
        Assert.Equal(["buffer 100 30", "window 0 0 99 29"], console.Calls);
    }

    [Fact]
    public void An_already_wide_window_and_buffer_are_untouched()
    {
        var console = new FakeConsole(new(140, 300, 0, 20, 139, 59));
        Assert.True(ConsoleLaunchSize.Apply(console, 100));
        Assert.Empty(console.Calls);
    }

    [Fact]
    public void A_wide_buffer_with_a_narrow_window_also_needs_its_window_widened()
    {
        var console = new FakeConsole(new(140, 300, 0, 20, 69, 59));
        Assert.True(ConsoleLaunchSize.Apply(console, 100));
        Assert.Equal(["window 0 20 99 59"], console.Calls);
    }

    [Fact]
    public void A_terminal_that_reports_success_but_keeps_a_narrow_window_is_not_sized()
    {
        var console = new FakeConsole(new(100, 30, 0, 0, 69, 29)) { IgnoreWindow = true };
        Assert.False(ConsoleLaunchSize.Apply(console, 100));
        Assert.Single(console.Calls);
    }

    [Fact]
    public void Buffer_growth_preserves_a_scrolled_windows_position_and_existing_height()
    {
        var console = new FakeConsole(new(80, 300, 10, 200, 79, 239));
        Assert.True(ConsoleLaunchSize.Apply(console, 100));
        Assert.Equal(["buffer 110 300", "window 10 200 109 239"], console.Calls);
    }

    [Fact]
    public async Task Launch_sizing_requests_100_columns_and_reapplies_only_on_the_first_read()
    {
        var injector = new FakeInjector();
        var started = new Started(42, DateTimeOffset.UtcNow);
        var sizing = new LaunchedConsoleSize(injector, started, _ => { });
        await sizing.AtLaunchAsync();
        await sizing.FirstReadAsync();
        await sizing.FirstReadAsync();
        Assert.Equal([(42, started.StartedAt, 100), (42, started.StartedAt, 100)], injector.Calls);
    }

    [Fact]
    public async Task Failed_sizing_is_bounded_and_a_narrow_working_box_still_refuses()
    {
        var console = new FakeConsole(new(70, 25, 0, 0, 69, 24)) { RefuseWindow = true };
        Assert.False(ConsoleLaunchSize.Apply(console, 100));
        var injector = new FakeInjector { Refuse = true };
        var logs = new List<string>();
        var sizing = new LaunchedConsoleSize(injector, new(42, DateTimeOffset.UtcNow), logs.Add);
        await sizing.AtLaunchAsync();
        await sizing.FirstReadAsync();
        await sizing.FirstReadAsync();
        Assert.Equal(2, injector.Calls.Count);
        Assert.Equal(2, logs.Count);
        var read = CopilotInputBoxReader.Read(
            ["╻" + new string('▄', 69), "┃ ", "╹" + new string('▀', 69), "Working · esc interrupt"]);
        Assert.Null(read.Box);
        Assert.Equal("Copilot's window at the machine is too narrow to check its input box; reply once the turn ends, or widen it", read.Problem);
    }

    sealed class FakeConsole(ConsoleGeometry initial) : IConsoleSize
    {
        ConsoleGeometry geometry = initial;
        public List<string> Calls { get; } = [];
        public bool RefuseWindow { get; init; }
        public bool IgnoreWindow { get; init; }
        public bool Read(out ConsoleGeometry value) { value = geometry; return true; }
        public bool Buffer(short columns, short rows)
        {
            Calls.Add($"buffer {columns} {rows}");
            geometry = geometry with { Columns = columns, Rows = rows };
            return true;
        }
        public bool Window(short left, short top, short right, short bottom)
        {
            Calls.Add($"window {left} {top} {right} {bottom}");
            if (RefuseWindow) { return false; }
            if (IgnoreWindow) { return true; }
            geometry = geometry with { Left = left, Top = top, Right = right, Bottom = bottom };
            return true;
        }
    }

    sealed class FakeInjector : AgentBridge.Host.Delivery.IConsoleInjector
    {
        public List<(int, DateTimeOffset, int)> Calls { get; } = [];
        public bool Refuse { get; init; }
        public Task<AgentBridge.Host.Delivery.InjectionResult> InjectAsync(int processId, AgentBridge.Host.Delivery.KeyPlan plan, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sizing must never type.");
        public Task<AgentBridge.Host.Delivery.ConsoleWidth> SizeForLaunchAsync(int processId, DateTimeOffset createdAt, int columns, CancellationToken cancellationToken = default)
        {
            Calls.Add((processId, createdAt, columns));
            return Task.FromResult(new AgentBridge.Host.Delivery.ConsoleWidth(Refuse ? null : columns, Refuse ? "cannot widen" : null, false));
        }
    }
}
