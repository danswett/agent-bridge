using AgentBridge.App.Core;
using Xunit;

public sealed class MachineLoadBarsTests
{
    static readonly MachineLoadValues Sample = new("fresh", 2000, 15000, 37.5, 32d * 1024 * 1024 * 1024, 8d * 1024 * 1024 * 1024);
    static void NoFigures(MachineLoadBars bars)
    {
        Assert.Null(bars.Cpu.Percent); Assert.Null(bars.Memory.Percent);
        Assert.Equal("no figure", bars.Cpu.Text); Assert.Equal("no figure", bars.Memory.Text);
    }

    [Fact]
    public void Bars_show_exact_whole_machine_CPU_and_used_memory()
    {
        var bars = MachineLoadBars.From(Sample);
        Assert.Equal(("fresh", "Fresh", "\u25cf", 2000d), (bars.State, bars.Word, bars.Icon, bars.AgeMs));
        Assert.Equal(new MachineLoadBar("CPU (whole machine)", 37.5, "37.5%"), bars.Cpu);
        Assert.Equal(new MachineLoadBar("Memory used", 75, "75%"), bars.Memory);
        Assert.Equal("37.543%", MachineLoadBars.From(Sample with { CpuPercent = 37.543 }).Cpu.Text);
        Assert.Equal("12.3%", MachineLoadBars.From(Sample with { TotalBytes = 10000, AvailableBytes = 8766 }).Memory.Text);
    }

    [Fact]
    public void Partial_keeps_measured_memory_and_missing_CPU_is_not_zero()
    {
        var bars = MachineLoadBars.From(Sample with { State = "partial", CpuPercent = null });
        Assert.Equal(("partial", "Partial", "\u25d0"), (bars.State, bars.Word, bars.Icon));
        Assert.Null(bars.Cpu.Percent); Assert.Equal("no figure", bars.Cpu.Text);
        Assert.Equal(75, bars.Memory.Percent);
        Assert.Equal("partial", MachineLoadBars.From(Sample with { TotalBytes = null }).State);
        NoFigures(MachineLoadBars.From(Sample with { CpuPercent = null, TotalBytes = null }));
    }

    [Fact]
    public void Measured_zero_is_still_a_real_figure()
    {
        var bars = MachineLoadBars.From(Sample with { CpuPercent = 0, TotalBytes = 100, AvailableBytes = 100 });
        Assert.Equal(0, bars.Cpu.Percent); Assert.Equal("0%", bars.Cpu.Text);
        Assert.Equal(0, bars.Memory.Percent); Assert.Equal("0%", bars.Memory.Text);
    }

    [Fact]
    public void Stale_unavailable_offline_and_unknown_never_repeat_live_figures()
    {
        foreach (var (load, online, state, word, icon) in new[]
        {
            (Sample with { State = "stale" }, true, "stale", "Stale", "\u25cc"),
            (Sample with { AgeMs = 15001 }, true, "stale", "Stale", "\u25cc"),
            (Sample with { State = "unavailable" }, true, "unavailable", "Unavailable", "\u25cb"),
            (Sample, false, "offline", "Offline", "\u2298"),
            (Sample with { State = "future" }, true, "unavailable", "Unavailable", "\u25cb"),
            (Sample with { State = "offline" }, true, "unavailable", "Unavailable", "\u25cb"),
        })
        {
            var bars = MachineLoadBars.From(load, online);
            Assert.Equal((state, word, icon), (bars.State, bars.Word, bars.Icon)); NoFigures(bars);
        }
        NoFigures(MachineLoadBars.From(null));
        Assert.Equal("fresh", MachineLoadBars.From(Sample with { AgeMs = 15000 }).State);
        Assert.Equal("fresh", MachineLoadBars.From(Sample with { AgeMs = 50000, StaleAfterMs = 60000 }).State);
    }

    [Fact]
    public void Invalid_figures_are_missing_rather_than_measured_load()
    {
        foreach (var cpu in new[] { -1d, 101d, double.NaN, double.PositiveInfinity })
        {
            var bars = MachineLoadBars.From(Sample with { CpuPercent = cpu, TotalBytes = 100, AvailableBytes = 101 });
            Assert.Equal("partial", bars.State); NoFigures(bars);
        }
        foreach (var total in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Assert.Null(MachineLoadBars.From(Sample with { TotalBytes = total }).Memory.Percent);
        Assert.Null(MachineLoadBars.From(Sample with { AvailableBytes = -1 }).Memory.Percent);
        Assert.Null(MachineLoadBars.From(Sample with { AgeMs = -1 }).AgeMs);
    }
}
