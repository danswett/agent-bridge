using System.Text;
using System.Text.Json;
using AgentBridge.HomeAssistant;
using Xunit;

public sealed class SettingsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abhs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    static (HaSettings Settings, IReadOnlyList<string> Problems) Read(string json) => HaSettings.Read(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_connector_is_off_unless_turned_on()
    {
        var (absent, none) = HaSettings.Read(null);
        Assert.False(absent.Enabled);
        Assert.Empty(none);
        Assert.False(Read("{}").Settings.Enabled);
        Assert.Equal("agent-decisions", absent.DashboardUrlPath);
        Assert.True(absent.DetailedActivity);
    }

    [Fact]
    public void A_setting_that_is_wrong_turns_the_connector_off_and_says_why()
    {
        foreach (var json in new[]
        {
            """{"enabled":"yes","baseUrl":"http://ha.test:8123"}""",
            """{"enabled":true}""",
            """{"enabled":true,"baseUrl":"ftp://ha.test"}""",
            """{"enabled":true,"baseUrl":"http://user:pass@ha.test"}""",
            """{"enabled":true,"baseUrl":"http://ha.test","dashboardUrlPath":"../x"}""",
            """{"enabled":true,"baseUrl":"http://ha.test","disposable":1}""",
        })
        {
            var (settings, problems) = Read(json);
            Assert.False(settings.Enabled, json);
            Assert.NotEmpty(problems);
            Assert.All(problems, p => Assert.EndsWith("the Home Assistant connector is off", p, StringComparison.Ordinal));
        }
        var (ok, none) = Read("""{"enabled":true,"baseUrl":"https://ha.test:8123","disposable":true,"machineName":"REHEARSAL-VM"}""");
        Assert.True(ok.Enabled);
        Assert.Empty(none);
        Assert.Equal("REHEARSAL-VM", ok.MachineName);
    }

    [Fact]
    public void A_development_build_only_ever_writes_to_a_home_assistant_said_to_be_disposable()
    {
        var on = new HaSettings { Enabled = true, BaseUrl = new Uri("http://ha.test:8123") };
        Assert.Contains("development build", HaGate.WhyNot(on, devBuild: true, tokenStored: true, canCheckAgentHaBridge: true), StringComparison.Ordinal);
        Assert.Null(HaGate.WhyNot(on with { Disposable = true }, devBuild: true, tokenStored: true, canCheckAgentHaBridge: true));
        Assert.Null(HaGate.WhyNot(on, devBuild: false, tokenStored: true, canCheckAgentHaBridge: true));
        Assert.Contains("token", HaGate.WhyNot(on, devBuild: false, tokenStored: false, canCheckAgentHaBridge: true), StringComparison.Ordinal);
        Assert.Contains("cannot be checked", HaGate.WhyNot(on, devBuild: false, tokenStored: true, canCheckAgentHaBridge: false), StringComparison.Ordinal);
        Assert.Contains("off", HaGate.WhyNot(HaSettings.Default, devBuild: false, tokenStored: true, canCheckAgentHaBridge: true), StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_is_kept_protected_beside_the_device_key_and_never_in_plain_text_on_windows()
    {
        const string token = "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.fixture-not-a-real-token";
        Assert.False(HaTokenStore.Exists(folder));
        HaTokenStore.Save(folder, token + "\n");
        Assert.True(HaTokenStore.Exists(folder));
        Assert.Equal(token, HaTokenStore.Load(folder));
        var files = Directory.GetFiles(folder);
        var file = Assert.Single(files);
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("ha-token.dpapi", file, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-not-a-real-token", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        HaTokenStore.Clear(folder);
        Assert.False(HaTokenStore.Exists(folder));
        Assert.Null(HaTokenStore.Load(folder));
    }

    [Fact]
    public void Phone_notifications_are_read_under_the_daemons_names_and_a_wrong_one_turns_the_connector_off()
    {
        Assert.Equal(HaNotifySettings.Default, Read("{}").Settings.Notify);
        Assert.False(HaNotifySettings.Default.ButtonsOn);
        var (settings, none) = Read("""
            {"enabled":true,"baseUrl":"http://ha.test:8123",
             "notifications":{"enabled":true,"service":"ticker.notify","tickerCategory":"agents"},
             "answerButtons":{"services":["notify.mobile_app_phone"," notify.mobile_app_phone "],"maxButtons":3,"icon":"","interruptionLevel":"active"}}
            """);
        Assert.Empty(none);
        Assert.True(settings.Enabled);
        var n = settings.Notify;
        Assert.True(n.Enabled);
        Assert.Equal("ticker.notify", n.Service);
        Assert.Equal("agents", n.TickerCategory);
        Assert.Equal(["notify.mobile_app_phone"], n.AnswerButtonServices);
        Assert.Equal(3, n.MaxButtons);
        Assert.Equal("", n.Icon);
        Assert.Equal("active", n.InterruptionLevel);
        Assert.Equal("#FF9F0A", n.Color);
        Assert.True(n.ButtonsOn);
        Assert.False((n with { AnswerButtons = false }).ButtonsOn);

        foreach (var part in new[]
        {
            """ "notifications":"on" """,
            """ "notifications":{"enabled":"yes"} """,
            """ "notifications":{"service":"notify"} """,
            """ "answerButtons":"on" """,
            """ "answerButtons":{"services":"notify.mobile_app_phone"} """,
            """ "answerButtons":{"services":["phone"]} """,
            """ "answerButtons":{"maxButtons":-1} """,
            """ "answerButtons":{"icon":3} """,
        })
        {
            var (wrong, problems) = Read($$"""{"enabled":true,"baseUrl":"http://ha.test",{{part}}}""");
            Assert.False(wrong.Enabled, part);
            Assert.Contains(problems, p => p.StartsWith("homeAssistant.notifications", StringComparison.Ordinal) || p.StartsWith("homeAssistant.answerButtons", StringComparison.Ordinal));
        }
    }
}