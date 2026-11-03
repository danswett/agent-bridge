using AgentBridge.Host.Delivery.Mac;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Xunit;

// The native exec fixture opens/closes pipes in this test process. Keep that churn
// outside other collections that assert a stable native descriptor snapshot.
[CollectionDefinition("Mac G5 generation", DisableParallelization = true)]
public sealed class MacG5GenerationCollection { }

[Collection("Mac G5 generation")]
public sealed class MacAgentHaBridgeProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_incomplete_scan_is_retried_with_a_fresh_complete_result(bool active)
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        MacDaemonObservation Read() => ++calls < 3 ? new(false, 501, [])
            : new(true, 501, active ? [new(123, 501, 501, 501)] : []);
        var probe = new MacAgentHaBridgeProbe(() => MacAgentHaBridgeProbe.ObserveBounded(Read, delays.Add));
        Assert.Equal(active, probe.Active() is not null);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25) }, delays);
    }

    [Fact]
    public void Three_incomplete_scans_still_hold_delivery_without_more_retries()
    {
        var calls = 0;
        var delays = 0;
        var probe = new MacAgentHaBridgeProbe(() => MacAgentHaBridgeProbe.ObserveBounded(
            () => { calls++; return new(false, 501, []); }, _ => delays++));
        Assert.NotNull(probe.Active());
        Assert.Equal(3, calls);
        Assert.Equal(2, delays);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void A_complete_scan_or_seen_PowerShell_runtime_returns_without_retry(bool complete, bool active)
    {
        var calls = 0;
        var delays = 0;
        var probe = new MacAgentHaBridgeProbe(() => MacAgentHaBridgeProbe.ObserveBounded(
            () => { calls++; return new(complete, 501, active ? [new(123, 501, 501, 501)] : []); },
            _ => delays++));
        Assert.Equal(active, probe.Active() is not null);
        Assert.Equal(1, calls);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void A_later_call_never_reuses_an_earlier_complete_clear_scan()
    {
        var calls = 0;
        var probe = new MacAgentHaBridgeProbe(() => MacAgentHaBridgeProbe.ObserveBounded(
            () => ++calls == 1 ? new(true, 501, []) : new(false, 501, []), _ => { }));
        Assert.Null(probe.Active());
        Assert.NotNull(probe.Active());
        Assert.Equal(4, calls);
    }

    sealed class Native : IMacDaemonNative
    {
        public uint? UserId { get; set; } = 501;
        public int[] Pids { get; set; } = [123];
        public List<int> Capacities { get; } = [];
        public List<int> PathReads { get; } = [];
        public List<int> GenerationReads { get; } = [];
        public Func<int[]?, int>? List { get; set; }
        public Func<int, MacDaemonOwnerRead> Owner { get; set; } = pid => new(new(pid, 501, 501, 501));
        public Func<int, MacDaemonGenerationRead> Generation { get; set; } = _ => new(new(100, 2));
        public Func<int, string?> Path { get; set; } = _ => "/usr/bin/ordinary-process";
        public int ListPids(int[]? buffer)
        {
            Capacities.Add(buffer?.Length ?? 0);
            if (List is not null) { return List(buffer); }
            if (buffer is null) { return Pids.Length; }
            Array.Copy(Pids, buffer, Math.Min(Pids.Length, buffer.Length));
            return Math.Min(Pids.Length, buffer.Length);
        }
        public MacDaemonOwnerRead ReadOwner(int pid) => Owner(pid);
        public MacDaemonGenerationRead ReadGeneration(int pid) { GenerationReads.Add(pid); return Generation(pid); }
        public string? ReadPath(int pid) { PathReads.Add(pid); return Path(pid); }
    }

    [Fact]
    public void A_complete_scan_without_a_PowerShell_runtime_allows_the_managed_backend()
    {
        var probe = new MacAgentHaBridgeProbe(() => new(true, 501, []));
        Assert.Null(probe.Active());
    }

    [Theory]
    [InlineData(501, 501, 501)]
    [InlineData(0, 501, 0)]
    [InlineData(0, 0, 501)]
    public void Any_runtime_that_can_act_as_this_user_holds_delivery(uint effective, uint real, uint saved)
    {
        var probe = new MacAgentHaBridgeProbe(() => new(true, 501, [new(123, effective, real, saved)]));
        Assert.Contains("pid 123", probe.Active());
    }

    [Fact]
    public void A_verified_different_users_runtime_does_not_hold_this_users_delivery()
    {
        var probe = new MacAgentHaBridgeProbe(() => new(true, 501, [new(123, 502, 502, 502)]));
        Assert.Null(probe.Active());
    }

    [Fact]
    public void An_incomplete_or_failed_process_scan_cannot_allow_delivery()
    {
        var incomplete = new MacAgentHaBridgeProbe(() => new(false, 501, []));
        Assert.NotNull(incomplete.Active());
        var failed = new MacAgentHaBridgeProbe(() => throw new IOException("unreadable"));
        Assert.NotNull(failed.Active());
    }

    [Fact]
    public void An_unreadable_current_user_executable_cannot_disappear_from_the_scan()
    {
        var native = new Native { Path = _ => null };
        var scan = MacAgentHaBridgeProbe.Observe(native);
        Assert.False(scan.Complete);
        Assert.NotNull(new MacAgentHaBridgeProbe(() => scan).Active());
        Assert.Equal([123, 123], native.PathReads);
    }

    [Fact]
    public void A_proven_foreign_process_does_not_require_its_protected_executable_path()
    {
        var native = new Native
        {
            Owner = pid => new(new(pid, 502, 502, 502)),
            Generation = _ => throw new IOException("generation is protected for another user"),
            Path = _ => throw new IOException("protected"),
        };
        var scan = MacAgentHaBridgeProbe.Observe(native);
        Assert.True(scan.Complete);
        Assert.Null(new MacAgentHaBridgeProbe(() => scan).Active());
        Assert.Empty(native.PathReads);
        Assert.Empty(native.GenerationReads);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(13, false)]
    [InlineData(3, true)]
    public void Only_ESRCH_proves_an_unreadable_identity_has_disappeared(int error, bool complete)
    {
        var native = new Native { Generation = _ => new(null, error) };
        Assert.Equal(complete, MacAgentHaBridgeProbe.Observe(native).Complete);
        Assert.Empty(native.PathReads);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(13, false)]
    [InlineData(3, true)]
    public void An_unknown_owner_cannot_be_assumed_to_belong_to_someone_else(int error, bool complete)
    {
        var native = new Native { Owner = _ => new(null, error) };
        Assert.Equal(complete, MacAgentHaBridgeProbe.Observe(native).Complete);
        Assert.Empty(native.GenerationReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_changed_kernel_generation_or_exec_version_holds_delivery(bool reusedPid)
    {
        var calls = 0;
        var native = new Native
        {
            Generation = _ => new(++calls == 1 ? new(100, 2) : reusedPid ? new(101, 2) : new(100, 3)),
        };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("effective")]
    [InlineData("real")]
    [InlineData("saved")]
    public void An_owner_change_during_path_lookup_holds_delivery(string change)
    {
        var calls = 0;
        var native = new Native
        {
            Owner = pid => new(++calls < 3 ? new(pid, 501, 501, 501) : change switch
            {
                "pid" => new(pid + 1, 501, 501, 501),
                "effective" => new(pid, 502, 501, 501),
                "real" => new(pid, 501, 502, 501),
                _ => new(pid, 501, 501, 502),
            }),
        };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
    }

    [Theory]
    [InlineData("/usr/bin/login", true)]
    [InlineData("/somewhere/ordinary-process", true)]
    [InlineData("/opt/homebrew/bin/pwsh", false)]
    public void A_stable_mixed_UID_process_is_classified_by_its_kernel_executable_not_a_full_BSD_query(string path, bool clear)
    {
        var native = new Native { Owner = pid => new(new(pid, 0, 501, 0)), Path = _ => path };
        var scan = MacAgentHaBridgeProbe.Observe(native);
        Assert.True(scan.Complete);
        Assert.Equal(clear, new MacAgentHaBridgeProbe(() => scan).Active() is null);
        Assert.Equal([123, 123], native.GenerationReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative/pwsh")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/usr/bin/ordinary\0hidden")]
    [InlineData("/usr/bin/changed")]
    public void Missing_invalid_or_changed_paths_never_prove_a_complete_scan(string? after)
    {
        var calls = 0;
        var native = new Native { Path = _ => ++calls == 1 ? "/usr/bin/ordinary" : after };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 22)]
    [InlineData(55, 0)]
    [InlineData(57, 0)]
    public void Failed_unsupported_or_short_generation_replies_hold_delivery(int bytes, int error)
    {
        var read = MacDaemonNative.DecodeGeneration(bytes, error, new() { UniqueId = 100, ExecVersion = 2 });
        Assert.Null(read.Generation);
        Assert.False(MacAgentHaBridgeProbe.Observe(new Native { Generation = _ => read }).Complete);
    }

    [Fact]
    public void A_zero_unique_ID_is_unknown_and_reserved_fields_are_not_identity()
    {
        var missing = MacDaemonNative.DecodeGeneration(56, 0, new() { ExecVersion = 2 });
        Assert.Null(missing.Generation);
        Assert.False(MacAgentHaBridgeProbe.Observe(new Native { Generation = _ => missing }).Complete);
        Assert.False(MacAgentHaBridgeProbe.Observe(new Native { Generation = _ => new(new(0, 2)) }).Complete);
        var a = MacDaemonNative.DecodeGeneration(56, 1, new() { UniqueId = 100, ExecVersion = 2 });
        var b = MacDaemonNative.DecodeGeneration(56, 0, new() { UniqueId = 100, ExecVersion = 2, Reserved2 = 1, Reserved3 = 2, ParentUniqueId = 3 });
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void An_owner_that_becomes_unreadable_inside_the_generation_window_holds_delivery(int failedRead)
    {
        var calls = 0;
        var native = new Native { Owner = pid => ++calls == failedRead ? new(null, 1) : new(new(pid, 501, 501, 501)) };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_malformed_owner_PID_is_not_excluded_even_if_it_claims_to_be_a_zombie(int failedRead)
    {
        var calls = 0;
        var native = new Native { Owner = pid => new(++calls == failedRead ? new(pid + 1, 501, 501, 501, true) : new(pid, 501, 501, 501)) };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(22)]
    public void An_unreadable_final_generation_cannot_publish_the_path_classification(int error)
    {
        var calls = 0;
        var native = new Native { Generation = _ => ++calls == 1 ? new(new(100, 2)) : new(null, error) };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
    }

    [Fact]
    public void Native_path_replies_that_are_full_oversized_empty_or_invalid_UTF8_are_unknown()
    {
        Assert.Null(MacDaemonNative.DecodePath([47, 97], 2));
        Assert.Null(MacDaemonNative.DecodePath([47, 97], 3));
        Assert.Null(MacDaemonNative.DecodePath([0], 0));
        Assert.Null(MacDaemonNative.DecodePath([47, 0xff, 0], 2));
        Assert.Equal("/a", MacDaemonNative.DecodePath([47, 97, 0], 2));
    }

    [Fact]
    public async Task A_fixture_exec_keeps_its_unique_ID_but_changes_its_native_version()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        var start = new ProcessStartInfo("/bin/bash")
        {
            ArgumentList = { "--noprofile", "--norc", "-c", "printf 'ready\\n'; read fixture; exec /bin/sleep 10" },
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        start.Environment.Remove("BASH_ENV");
        using var process = Process.Start(start)!;
        try
        {
            // macOS /bin/sh can itself exec bash during startup. Wait until this
            // explicitly chosen shell reaches our fixture before capturing its token.
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            var native = new MacDaemonNative();
            var before = native.ReadGeneration(process.Id).Generation;
            Assert.NotNull(before);
            Assert.Equal("/bin/bash", native.ReadPath(process.Id));
            await process.StandardInput.WriteLineAsync("continue");
            await process.StandardInput.FlushAsync();
            var until = Stopwatch.StartNew();
            while (native.ReadPath(process.Id) != "/bin/sleep" && until.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(10); }
            Assert.Equal("/bin/sleep", native.ReadPath(process.Id));
            var after = native.ReadGeneration(process.Id).Generation;
            Assert.NotNull(after);
            Assert.Equal(before.UniqueId, after.UniqueId);
            Assert.NotEqual(before.ExecVersion, after.ExecVersion);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); }
            await process.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData("/opt/homebrew/bin/pwsh")]
    [InlineData("/somewhere/PowerShell")]
    public void A_complete_raw_scan_identifies_the_current_users_PowerShell_runtime(string path)
    {
        var native = new Native { Path = _ => path };
        var scan = MacAgentHaBridgeProbe.Observe(native);
        Assert.True(scan.Complete);
        Assert.Equal(123, Assert.Single(scan.Processes).ProcessId);
        Assert.NotNull(new MacAgentHaBridgeProbe(() => scan).Active());
    }

    [Fact]
    public void Codex_discovery_reuses_complete_owner_checks_without_changing_the_G5_name_policy()
    {
        var native = new Native
        {
            Pids = [100, 101, 102, 103, 104, 105],
            Path = pid => pid switch
            {
                100 => "/test/codex", 101 => "/test/node", 102 => "/test/bun", 103 => "/test/codex.exe",
                104 => "/test/codex-app-server", _ => "/test/pwsh",
            },
        };
        Assert.Equal(new[] { 100, 101, 102, 103, 104 }, ManagedSessionProcessInspector.NativeCandidates(native));
        Assert.Equal(105, Assert.Single(MacAgentHaBridgeProbe.Observe(native).Processes).ProcessId);
        native.Path = _ => null;
        Assert.Null(ManagedSessionProcessInspector.NativeCandidates(native));
        native.Owner = pid => new(new(pid, 502, 502, 502));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<int>>(ManagedSessionProcessInspector.NativeCandidates(native)));
    }

    [Fact]
    public void A_full_PID_buffer_is_retried_until_a_complete_snapshot_is_obtained()
    {
        var calls = 0;
        var native = new Native { List = buffer =>
        {
            if (buffer is null) { return 1; }
            if (++calls == 1) { return buffer.Length; }
            buffer[0] = 123;
            return 1;
        } };
        Assert.True(MacAgentHaBridgeProbe.Observe(native).Complete);
        Assert.Equal([0, 128, 256], native.Capacities);
    }

    [Fact]
    public void Repeatedly_truncated_or_invalid_PID_lists_never_claim_completeness()
    {
        var truncated = new Native { List = buffer => buffer?.Length ?? 1 };
        Assert.False(MacAgentHaBridgeProbe.Observe(truncated).Complete);
        Assert.Equal(5, truncated.Capacities.Count);
        foreach (var pids in new[] { new[] { -1 }, new[] { 0 }, new[] { 123, 123 } })
        { Assert.False(MacAgentHaBridgeProbe.Observe(new Native { Pids = pids }).Complete); }
        Assert.False(MacAgentHaBridgeProbe.Observe(new Native { List = _ => int.MaxValue }).Complete);
        Assert.False(MacAgentHaBridgeProbe.Observe(new Native { UserId = null }).Complete);
    }

    [Fact]
    public void Native_layouts_preserve_the_unique_ID_exec_version_and_owner_offsets()
    {
        Assert.Equal(56, Marshal.SizeOf<MacDaemonNative.UniqueInfo>());
        Assert.Equal(16, Marshal.OffsetOf<MacDaemonNative.UniqueInfo>(nameof(MacDaemonNative.UniqueInfo.UniqueId)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<MacDaemonNative.UniqueInfo>(nameof(MacDaemonNative.UniqueInfo.ExecVersion)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<MacDaemonNative.ShortBsdInfo>());
        Assert.Equal(36, Marshal.OffsetOf<MacDaemonNative.ShortBsdInfo>(nameof(MacDaemonNative.ShortBsdInfo.Uid)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<MacDaemonNative.ShortBsdInfo>(nameof(MacDaemonNative.ShortBsdInfo.RealUid)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<MacDaemonNative.ShortBsdInfo>(nameof(MacDaemonNative.ShortBsdInfo.SavedUid)).ToInt32());
    }
}
