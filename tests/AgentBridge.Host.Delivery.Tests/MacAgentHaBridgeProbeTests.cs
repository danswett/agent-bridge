using AgentBridge.Host.Delivery.Mac;
using System.Runtime.InteropServices;
using Xunit;

public sealed class MacAgentHaBridgeProbeTests
{
    sealed class Native : IMacDaemonNative
    {
        public uint? UserId { get; set; } = 501;
        public int[] Pids { get; set; } = [123];
        public List<int> Capacities { get; } = [];
        public List<int> PathReads { get; } = [];
        public List<int> IdentityReads { get; } = [];
        public Func<int[]?, int>? List { get; set; }
        public Func<int, MacDaemonOwnerRead> Owner { get; set; } = pid => new(new(pid, 501, 501, 501));
        public Func<int, MacDaemonIdentityRead> Identity { get; set; } = pid => new(new(pid, 501, 501, 501, 100, 2));
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
        public MacDaemonIdentityRead ReadIdentity(int pid) { IdentityReads.Add(pid); return Identity(pid); }
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
        Assert.Equal([123], native.PathReads);
    }

    [Fact]
    public void A_proven_foreign_process_does_not_require_its_protected_executable_path()
    {
        var native = new Native
        {
            Owner = pid => new(new(pid, 502, 502, 502)),
            Identity = _ => throw new IOException("full BSDINFO is protected for other users"),
            Path = _ => throw new IOException("protected"),
        };
        var scan = MacAgentHaBridgeProbe.Observe(native);
        Assert.True(scan.Complete);
        Assert.Null(new MacAgentHaBridgeProbe(() => scan).Active());
        Assert.Empty(native.PathReads);
        Assert.Empty(native.IdentityReads);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(13, false)]
    [InlineData(3, true)]
    public void Only_ESRCH_proves_an_unreadable_identity_has_disappeared(int error, bool complete)
    {
        var native = new Native { Identity = _ => new(null, error) };
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
        Assert.Empty(native.IdentityReads);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("user")]
    public void A_changed_identity_during_path_lookup_holds_delivery(string change)
    {
        var calls = 0;
        var native = new Native
        {
            Identity = pid => new(++calls == 1 ? new(pid, 501, 501, 501, 100, 2) : change switch
            {
                "pid" => new(pid + 1, 501, 501, 501, 100, 2),
                "start" => new(pid, 501, 501, 501, 101, 2),
                _ => new(pid, 502, 502, 502, 100, 2),
            }),
        };
        Assert.False(MacAgentHaBridgeProbe.Observe(native).Complete);
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
    public void BSD_identity_layout_preserves_the_native_UID_and_start_time_offsets()
    {
        Assert.Equal(136, Marshal.SizeOf<MacDaemonNative.BsdInfo>());
        Assert.Equal(20, Marshal.OffsetOf<MacDaemonNative.BsdInfo>(nameof(MacDaemonNative.BsdInfo.Uid)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<MacDaemonNative.BsdInfo>(nameof(MacDaemonNative.BsdInfo.RealUid)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<MacDaemonNative.BsdInfo>(nameof(MacDaemonNative.BsdInfo.SavedUid)).ToInt32());
        Assert.Equal(120, Marshal.OffsetOf<MacDaemonNative.BsdInfo>(nameof(MacDaemonNative.BsdInfo.Seconds)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<MacDaemonNative.ShortBsdInfo>());
        Assert.Equal(36, Marshal.OffsetOf<MacDaemonNative.ShortBsdInfo>(nameof(MacDaemonNative.ShortBsdInfo.Uid)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<MacDaemonNative.ShortBsdInfo>(nameof(MacDaemonNative.ShortBsdInfo.RealUid)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<MacDaemonNative.ShortBsdInfo>(nameof(MacDaemonNative.ShortBsdInfo.SavedUid)).ToInt32());
    }
}
