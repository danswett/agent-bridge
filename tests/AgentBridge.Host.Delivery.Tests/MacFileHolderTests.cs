using AgentBridge.Host.Delivery.Mac;
using Xunit;

public sealed class MacFileHolderTests
{
    static readonly MacFileIdentity Target = new(1, 123, 0, 0x8180);
    static readonly MacFileIdentity Other = new(1, 124, 0, 0x8180);
    static MacProcessIdentity Identity(int pid) => new(pid, 1, 501, 501, 501, Fixture.T0, "/test/codex");

    sealed class Probe : IMacDescriptorProbe
    {
        public Func<string, MacFileIdentity?> ReadFile { get; set; } = _ => Target;
        public Func<int, MacProcessIdentity?> ReadProcess { get; set; } = Identity;
        public Func<int, IReadOnlyList<MacDescriptor>?> ReadDescriptors { get; set; } = _ => [new(3, 1), new(4, 2)];
        public Func<int, int, MacFileIdentity?> ReadVnode { get; set; } = (pid, _) => pid == 101 ? Target : Other;
        public MacFileIdentity? File(string path) => ReadFile(path);
        public MacProcessIdentity? Process(int pid) => ReadProcess(pid);
        public IReadOnlyList<MacDescriptor>? Descriptors(int pid) => ReadDescriptors(pid);
        public MacFileIdentity? Vnode(int pid, int fd) => ReadVnode(pid, fd);
    }

    [Fact]
    public void Complete_native_lists_distinguish_a_holder_from_a_readable_external_process()
    {
        var probe = new Probe();
        Assert.Equal(new[] { 101 }, MacFileHolders.Find("/test/lock", [101, 102], probe));
        probe.ReadVnode = (_, _) => Target;
        Assert.Equal(new[] { 101, 102 }, MacFileHolders.Find("/test/lock", [101, 102], probe));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("list")]
    [InlineData("vnode")]
    [InlineData("unknown-kind")]
    [InlineData("negative-fd")]
    [InlineData("duplicate-fd")]
    [InlineData("over-limit")]
    public void An_incomplete_external_process_prevents_a_falsely_unique_owned_holder(string failure)
    {
        var probe = new Probe();
        if (failure == "process") { probe.ReadProcess = pid => pid == 102 ? null : Identity(pid); }
        if (failure == "list") { probe.ReadDescriptors = pid => pid == 102 ? null : [new(3, 1)]; }
        if (failure == "vnode") { probe.ReadVnode = (pid, _) => pid == 102 ? null : Target; }
        if (failure is "unknown-kind" or "negative-fd" or "duplicate-fd" or "over-limit")
        {
            probe.ReadDescriptors = pid => pid == 101 ? [new(3, 1)] : failure switch
            {
                "unknown-kind" => [new(3, 8)],
                "negative-fd" => [new(-1, 1)],
                "duplicate-fd" => [new(3, 1), new(3, 2)],
                _ => Enumerable.Range(0, MacFileHolders.MaxDescriptors + 1).Select(i => new MacDescriptor(i, 2)).ToArray(),
            };
        }
        Assert.Empty(MacFileHolders.Find("/test/lock", [101, 102], probe));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("fd-list")]
    [InlineData("fd-reuse")]
    [InlineData("target")]
    public void Changes_during_native_enumeration_invalidate_the_whole_result(string change)
    {
        var probe = new Probe();
        var reads = 0;
        if (change == "process") { probe.ReadProcess = pid => Identity(pid) with { StartedAt = Fixture.T0.AddSeconds(reads++) }; }
        if (change == "fd-list") { probe.ReadDescriptors = _ => reads++ == 0 ? [new(3, 1)] : [new(3, 1), new(4, 2)]; }
        if (change == "fd-reuse") { probe.ReadVnode = (_, _) => reads++ == 0 ? Target : Other; }
        if (change == "target") { probe.ReadFile = _ => reads++ == 0 ? Target : Other; }
        Assert.Empty(MacFileHolders.Find("/test/lock", [101], probe));
    }

    [Fact]
    public void Symlinks_and_exhausted_traversal_budgets_do_not_produce_holders()
    {
        var probe = new Probe { ReadFile = _ => Target with { Mode = 0xa180 } };
        Assert.Empty(MacFileHolders.Find("/test/link", [101], probe));
        probe.ReadFile = _ => Target;
        var calls = 0;
        Assert.Empty(MacFileHolders.Find("/test/lock", [101], probe, () => ++calls < 4));
        Assert.Empty(MacFileHolders.Find("/test/lock", [101, 101], probe));
    }
}
