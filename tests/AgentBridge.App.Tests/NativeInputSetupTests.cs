using Xunit;

namespace AgentBridge.App.Tests;

public sealed class NativeInputSetupTests
{
    [Fact]
    public void A_background_window_fails_before_any_later_input()
    {
        var attempts = 0;
        var posted = false;
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            NativeInputSetup.RequireForeground(() => { attempts++; return false; },
                () => NativeInputSetup.Foreground("CoreWindow", "Microsoft account", "WWAHost"));
            posted = true;
        });
        Assert.Equal(2, attempts);
        Assert.False(posted);
        Assert.Equal("setup: window not foreground (class: \"CoreWindow\"; title: \"Microsoft account\"; process: \"WWAHost\")", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Acquiring_foreground_on_either_attempt_allows_the_case(int successOn)
    {
        var attempts = 0;
        NativeInputSetup.RequireForeground(() => ++attempts == successOn,
            () => throw new InvalidOperationException("Owner lookup must not run after success."));
        Assert.Equal(successOn, attempts);
    }

    [Fact]
    public void Foreground_diagnostics_keep_owner_fields_on_one_line() =>
        Assert.Equal("class: \"unknown\"; title: \"Microsoft\\r\\naccount\"; process: \"unknown\"",
            NativeInputSetup.Foreground(null, "Microsoft\r\naccount", ""));

    [Theory]
    [InlineData(10, 20, 10, 10, 7, 42, 7, 42, true)]
    [InlineData(0, 20, 10, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 0, 10, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 10, 10, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 20, 30, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 20, 10, 30, 7, 42, 7, 42, false)]
    [InlineData(10, 20, 10, 10, 0, 42, 0, 42, false)]
    [InlineData(10, 20, 10, 10, 7, 42, 8, 42, false)]
    [InlineData(10, 20, 10, 10, 7, 0, 7, 0, false)]
    [InlineData(10, 20, 10, 10, 7, 42, 7, 43, false)]
    public void Posted_input_requires_a_focused_child_of_the_foreground_app_on_its_thread_and_process(
        int window, int focus, int root, int foreground, uint thread, uint process, uint windowThread, uint windowProcess, bool ready) =>
        Assert.Equal(ready, NativeInputSetup.TargetReady(window, focus, root, foreground, thread, process, windowThread, windowProcess));
}
