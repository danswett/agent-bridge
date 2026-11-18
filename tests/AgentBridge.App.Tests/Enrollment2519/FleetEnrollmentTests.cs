using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Network")]
public sealed class FleetEnrollmentTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    HostConnection connection = null!;
    readonly List<SessionCardViewModel> cards = [];

    public async Task InitializeAsync()
    {
        host.Start();
        connection = host.Connect();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
    }

    public async Task DisposeAsync()
    {
        foreach (var card in cards) { card.Detach(); }
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    SessionCardViewModel Card(string? key = Machine.LocalKey, string? readOnly = null, AgentKind agent = AgentKind.Copilot)
    {
        var context = new CardContext
        {
            Host = connection, Deliveries = new Deliveries(connection, new LockedDispatcher()),
            Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, ReadOnlyWhy = () => readOnly,
        };
        if (key is not null) { _ = new Machine(key, connection, context); }
        var seeded = JsonSerializer.SerializeToNode(Make.Session() with
        {
            Agent = agent, Title = "Fixture title", Cwd = @"C:\fixture", Repository = "fixture-workspace",
        }, ProtocolJson.Options)!;
        seeded["haLabel"] = "Fixture HA label";
        var card = new SessionCardViewModel(seeded.Deserialize<Session>(ProtocolJson.Options)!, context);
        cards.Add(card);
        return card;
    }

    static PropertyInfo Property(string name)
    {
        var property = typeof(SessionCardViewModel).GetProperty(name);
        Assert.NotNull(property);
        return property;
    }
    static T Read<T>(SessionCardViewModel card, string name) => Assert.IsType<T>(Property(name).GetValue(card));
    static void Draft(SessionCardViewModel card, string value) => Property("FleetNameDraft").SetValue(card, value);
    static Task Save(SessionCardViewModel card)
    {
        var method = typeof(SessionCardViewModel).GetMethod("SaveFleetNameAsync");
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(card, null));
    }

    [Theory]
    [InlineData("\"present\"", "present", "Another live session uses this label")]
    [InlineData("\"absent\"", "absent", "No duplicate was found")]
    [InlineData("\"unknown\"", "unknown", "could not be checked")]
    [InlineData(null, "unknown", "could not be checked")]
    [InlineData("null", "unknown", "could not be checked")]
    [InlineData("7", "unknown", "could not be checked")]
    [InlineData("\"unexpected\"", "unknown", "could not be checked")]
    public async Task Disclosure_precedes_explicit_save_and_only_success_shows_duplicate_result(string? status, string expected, string warning)
    {
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync("session.rename", _ => gate.Task);
        var card = Card();
        Assert.True(Read<bool>(card, "CanEnrollFleetName"));
        Assert.Contains("The relay sees this label.", Read<string>(card, "FleetNameDisclosure"));
        Assert.Contains("Do not include private task details", Read<string>(card, "FleetNameDisclosure"));
        card.Update(card.Session with { Title = "Changed fixture title", Cwd = @"C:\other-fixture", Repository = "other-workspace" });
        Assert.Empty(Read<string>(card, "FleetNameDraft"));
        Assert.Empty(Read<string>(card, "FleetNameResult"));
        await connection.CallAsync(FakeHost.Barrier);
        Assert.Empty(host.Sent("session.rename"));
        Draft(card, "Public role");
        var saving = Save(card);
        await Wait.UntilAsync(() => host.Sent("session.rename").Any(), "rename sent");
        Assert.Empty(Read<string>(card, "FleetNameResult"));
        Assert.False(Read<bool>(card, "CanEnrollFleetName"));
        await Save(card);
        var reply = new JsonObject { ["id"] = card.Id, ["name"] = "Public role" };
        if (status is not null) { reply["duplicateStatus"] = JsonNode.Parse(status); }
        gate.SetResult(reply);
        await saving;
        Assert.Contains("saved", Read<string>(card, "FleetNameResult"));
        Assert.Contains($"Duplicate status: {expected}", Read<string>(card, "FleetNameResult"));
        Assert.Contains(warning, Read<string>(card, "FleetNameResult"));
        Assert.True(Read<bool>(card, "CanEnrollFleetName"));
        var sent = Assert.Single(host.Sent("session.rename"));
        Assert.Equal(card.Id, sent.GetProperty("id").GetString());
        Assert.Equal("Public role", sent.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Failed_save_clears_previous_success_and_blank_is_an_explicit_clear_without_retry()
    {
        host.On("session.rename", _ => new { duplicateStatus = "present" });
        var card = Card();
        await Save(card);
        Assert.Contains("saved", Read<string>(card, "FleetNameResult"));
        Assert.Equal("", Assert.Single(host.Sent("session.rename")).GetProperty("name").GetString());
        host.On("session.rename", _ => throw new HostErrorException("fixture refused"));
        await Save(card);
        Assert.DoesNotContain("saved", Read<string>(card, "FleetNameResult"));
        Assert.DoesNotContain("Another live", Read<string>(card, "FleetNameResult"));
        Assert.Contains("fixture refused", Read<string>(card, "FleetNameResult"));
        Assert.True(Read<bool>(card, "CanEnrollFleetName"));
        Assert.Equal(2, host.Sent("session.rename").Count());
    }

    [Theory]
    [InlineData("remote", null, AgentKind.Copilot)]
    [InlineData(null, null, AgentKind.Copilot)]
    [InlineData(Machine.LocalKey, "read-only", AgentKind.Copilot)]
    [InlineData(Machine.LocalKey, null, AgentKind.Unknown)]
    public async Task Ineligible_cards_never_send_a_rename(string? key, string? readOnly, AgentKind agent)
    {
        var card = Card(key, readOnly, agent);
        Assert.False(Read<bool>(card, "ShowFleetEnrollment"));
        Assert.False(Read<bool>(card, "CanEnrollFleetName"));
        await Save(card);
        Assert.Empty(host.Sent("session.rename"));
        Assert.DoesNotContain("saved", Read<string>(card, "FleetNameResult"));
    }

    [Fact]
    public void Details_discloses_before_input_and_save_and_announces_plain_text_result()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var xaml = XDocument.Load(Path.Combine(root, "src", "AgentBridge.App", "SessionDetails.xaml"));
        XElement Named(string id) => xaml.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == id);
        var disclosure = Named("FleetNameDisclosure");
        var panel = disclosure.Parent!;
        Assert.Equal(["FleetNameDisclosure", "FleetNameInput", "SaveFleetName", "FleetNameResult"],
            panel.Elements().Select(e => (string?)e.Attribute("AutomationProperties.AutomationId")));
        Assert.Contains("Card.ShowFleetEnrollment", (string)panel.Attribute("Visibility")!);
        Assert.Contains("Card.FleetNameDisclosure", (string)disclosure.Attribute("Text")!);
        Assert.Contains("Mode=TwoWay", (string)Named("FleetNameInput").Attribute("Text")!);
        Assert.Contains("Card.FleetNameDraft", (string)Named("FleetNameInput").Attribute("Text")!);
        Assert.Contains("Card.CanEnrollFleetName", (string)Named("SaveFleetName").Attribute("IsEnabled")!);
        Assert.Equal("OnSaveFleetName", (string)Named("SaveFleetName").Attribute("Click")!);
        Assert.Equal("TextBlock", Named("FleetNameResult").Name.LocalName);
        Assert.Contains("Card.FleetNameResult", (string)Named("FleetNameResult").Attribute("Text")!);
        Assert.Equal("Polite", (string)Named("FleetNameResult").Attribute("AutomationProperties.LiveSetting")!);
    }
}
