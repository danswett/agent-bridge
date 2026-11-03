using System.Diagnostics;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Mac;
using Xunit;

public sealed class ManagedTmuxFactAttribute : FactAttribute
{
    public ManagedTmuxFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) { Skip = "needs macOS process identities and an isolated tmux server"; }
        else if (!File.Exists("/opt/homebrew/bin/tmux") && !File.Exists("/usr/local/bin/tmux")) { Skip = "needs tmux"; }
    }
}

// Only echo programs authored here, on a fresh private server. No real agents, user
// profile, default tmux server or terminal focus is read or changed.
public sealed class ManagedTmuxIntegrationTests
{
    [ManagedTmuxFact]
    public async Task Terminal_choices_remain_raw_keys_even_when_the_application_enables_bracketed_paste()
    {
        var root = "/tmp/abtmux-" + Guid.NewGuid().ToString("N")[..12];
        var executable = File.Exists("/opt/homebrew/bin/tmux") ? "/opt/homebrew/bin/tmux" : "/usr/local/bin/tmux";
        var backend = new ManagedTmuxBackend(executable, root);
        var command = new TmuxCommand(executable, backend.SocketPath);
        try
        {
            foreach (var key in new[] { " ", "y", "n" })
            {
                var capture = Path.Combine(root, Guid.NewGuid().ToString("N") + ".txt");
                // This test-owned program requests bracketed paste, then records exactly
                // two raw input bytes. With paste-buffer -p they would be ESC,[ instead
                // of the intended choice and Enter, even though tmux reports success.
                const string read = "stty raw -echo; printf '\\033[?2004hREADY\\n'; dd bs=1 count=2 2>/dev/null | od -An -tx1 > \"$1\"; sleep 10; :";
                var started = backend.Start(new("/bin/bash", ["-c", read, "--", capture], root));
                var timer = Stopwatch.StartNew();
                while ((await backend.ReadScreenAsync(started.ProcessId)).Rows?.Any(r => r.Contains("READY", StringComparison.Ordinal)) != true)
                {
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the test-owned input reader did not become ready");
                    await Task.Delay(20);
                }
                Assert.Equal(InjectionStatus.Typed, (await backend.InjectAsync(started.ProcessId, new([new(key, true, 0, 0)]))).Status);
                while (!File.Exists(capture) || new FileInfo(capture).Length == 0)
                {
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the test-owned input reader did not record its bytes");
                    await Task.Delay(20);
                }
                var actual = File.ReadAllText(capture).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(new[] { ((int)key[0]).ToString("x2"), "0d" }, actual);
                using var held = Assert.IsAssignableFrom<IHeldProcess>(backend.Open(started.ProcessId, started.StartedAt).Process);
                Assert.Null(held.Terminate());
                Assert.True(await held.WaitForExitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally
        {
            await command.RunAsync(["kill-server"]);
            Directory.Delete(root, true);
        }
    }

    [ManagedTmuxFact]
    public async Task Real_private_pane_delivers_literal_reply_reads_screen_and_stops_only_its_echo()
    {
        var root = "/tmp/abtmux-" + Guid.NewGuid().ToString("N")[..12];
        var executable = File.Exists("/opt/homebrew/bin/tmux") ? "/opt/homebrew/bin/tmux" : "/usr/local/bin/tmux";
        var backend = new ManagedTmuxBackend(executable, root);
        var command = new TmuxCommand(executable, backend.SocketPath);
        try
        {
            var started = backend.Start(new("/bin/cat", [], root));
            Assert.Equal(MacProcessIdentityReader.CurrentUserId, MacProcessIdentityReader.Capture(started.ProcessId)?.UserId);
            using var actual = Process.GetProcessById(started.ProcessId);
            Assert.Equal(started.StartedAt, new DateTimeOffset(actual.StartTime.ToUniversalTime()));
            Assert.True(backend.Running(started));
            var words = "literal ; run-shell ignored; $(ignored) \\;";
            Assert.Equal(InjectionStatus.Typed, (await backend.InjectAsync(started.ProcessId, new([new(words, true, 0, 0)]))).Status);
            var timer = Stopwatch.StartNew();
            ConsoleScreen screen;
            do
            {
                screen = await backend.ReadScreenAsync(started.ProcessId);
                if (screen.Rows?.Contains(words) == true) { break; }
                await Task.Delay(25);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Contains(words, screen.Rows ?? []);
            using var held = Assert.IsAssignableFrom<IHeldProcess>(backend.Open(started.ProcessId, started.StartedAt).Process);
            Assert.Null(held.Terminate());
            Assert.True(await held.WaitForExitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(backend.Running(started));
        }
        finally
        {
            await command.RunAsync(["kill-server"]);
            Directory.Delete(root, true);
        }
    }

    [ManagedTmuxFact]
    public async Task Real_launch_arguments_cannot_create_a_second_tmux_command_and_reply_can_exit_gracefully()
    {
        var root = "/tmp/abtmux-" + Guid.NewGuid().ToString("N")[..12];
        var executable = File.Exists("/opt/homebrew/bin/tmux") ? "/opt/homebrew/bin/tmux" : "/usr/local/bin/tmux";
        var backend = new ManagedTmuxBackend(executable, root);
        var command = new TmuxCommand(executable, backend.SocketPath);
        try
        {
            // The only shell is this static test echo program, explicitly selected as
            // the executable. Data after -- is positional argv, never shell source.
            const string echo = "printf 'ARG:%s\\n' \"$1\"; while IFS= read -r line; do [ \"$line\" = /exit ] && exit 0; printf 'GOT:%s\\n' \"$line\"; done";
            var started = backend.Start(new("/bin/bash", ["-c", echo, "--", ";", "kill-server"], root));
            var screen = await backend.ReadScreenAsync(started.ProcessId);
            Assert.Contains("ARG:;", screen.Rows ?? []);
            using var held = Assert.IsAssignableFrom<IHeldProcess>(backend.Open(started.ProcessId, started.StartedAt).Process);
            Assert.Equal(InjectionStatus.Typed, (await backend.InjectAsync(started.ProcessId, new([new("/exit", true, 0, 0)]))).Status);
            Assert.True(await held.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await command.RunAsync(["kill-server"]);
            Directory.Delete(root, true);
        }
    }
}
