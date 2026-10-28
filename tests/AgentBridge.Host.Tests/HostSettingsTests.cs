using AgentBridge.Host;
using Xunit;

public sealed class HostSettingsTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("abset-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    (HostSettings Settings, string? Problem) With(string? text)
    {
        if (text is not null) { File.WriteAllText(Path.Combine(folder, HostSettings.FileName), text); }
        return HostSettings.Load(folder);
    }

    [Fact]
    public void Remote_approval_is_on_without_a_file_or_a_setting_and_off_only_when_set_false()
    {
        Assert.Equal((HostSettings.Default, (string?)null), With(null));
        Assert.True(With("{ \"permissions\": {} }").Settings.RemoteApproval);
        Assert.True(With("{ \"permissions\": { \"remoteApproval\": true } }").Settings.RemoteApproval);
        Assert.Equal((new HostSettings(false), (string?)null), With("{ // mine\n \"permissions\": { \"remoteApproval\": false, } }"));
    }

    [Theory]
    [InlineData("{ \"permissions\": { \"remoteApproval\": \"no\" } }")]
    [InlineData("{ \"permissions\": false }")]
    [InlineData("not json")]
    public void A_setting_that_cannot_be_read_as_true_or_false_turns_remote_approval_off_and_says_why(string text)
    {
        var (settings, problem) = With(text);

        Assert.False(settings.RemoteApproval);
        Assert.Contains("remote approval of permission prompts is off", problem);
    }
}
