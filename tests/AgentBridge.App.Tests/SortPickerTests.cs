using AgentBridge.App.Core;
using Xunit;

public sealed class SortPickerTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abo-" + Guid.NewGuid().ToString("N")[..8]);
    string File => Path.Combine(folder, "order.json");

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    MainViewModel Main(HostConnection connection) => new(connection, new CardContext
    {
        Deliveries = new Deliveries(connection, new LockedDispatcher()),
        Folds = new FoldStore(null),
        Capabilities = () => connection.Capabilities,
        Host = connection,
    }, new UsageViewModel(), order: new SessionOrder(File));

    [Fact]
    public async Task An_unwritable_order_disables_your_order_and_ignores_a_stale_selection()
    {
        await using var host = new FakeHost();
        await using var connection = host.Connect(start: false);
        Directory.CreateDirectory(File);
        var main = Main(connection);
        main.Order.SetPinned("first", true);

        Assert.False(main.Order.Usable);
        Assert.False(main.CanSort((int)SessionSort.Yours));
        Assert.True(main.CanSort((int)SessionSort.Recent));
        Assert.True(main.CanSort((int)SessionSort.Machine));

        Directory.Delete(File);
        main.SortIndex = (int)SessionSort.Yours;

        Assert.Equal((int)SessionSort.Recent, main.SortIndex);
        Assert.False(main.Order.Usable);
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public async Task A_usable_order_enables_your_order_and_keeps_the_selection()
    {
        await using var host = new FakeHost();
        await using var connection = host.Connect(start: false);
        var main = Main(connection);
        main.SortIndex = (int)SessionSort.Recent;

        Assert.True(main.CanSort((int)SessionSort.Yours));
        main.SortIndex = (int)SessionSort.Yours;

        Assert.Equal((int)SessionSort.Yours, main.SortIndex);
        Assert.Equal(SessionSort.Yours, new SessionOrder(File).Sort);
    }

    [Fact]
    public async Task A_later_successful_save_reenables_your_order_without_selecting_it()
    {
        await using var host = new FakeHost();
        await using var connection = host.Connect(start: false);
        Directory.CreateDirectory(File);
        var main = Main(connection);
        main.Order.SetPinned("first", true);
        Assert.False(main.CanSort((int)SessionSort.Yours));

        Directory.Delete(File);
        main.Order.SetPinned("second", true);

        Assert.True(main.Order.Usable);
        Assert.True(main.CanSort((int)SessionSort.Yours));
        Assert.Equal((int)SessionSort.Recent, main.SortIndex);
        Assert.Equal(SessionSort.Recent, new SessionOrder(File).Sort);
        main.SortIndex = (int)SessionSort.Yours;
        Assert.Equal((int)SessionSort.Yours, main.SortIndex);
    }
}
