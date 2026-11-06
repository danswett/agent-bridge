using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using Xunit;

// What a person is told when a launch fails: fixed words, never an exception's own text (#802, #1021).
// The exception goes to the host's log, which is where each of these tests finds it.
public sealed partial class LaunchFlowTests
{
    const string Recognisable = "SECRET-C:\\Users\\someone\\denied.dll";

    sealed class FetchThrows(Exception error) : ISessionFetcher
    {
        public string? Unavailable => null;

        public Task<FetchedBundle> FetchAsync(LaunchTransfer transfer, string peer, long maxBytes, CancellationToken cancellationToken) => throw error;
    }

    // A bundle that arrived, but that is not a zip: installing it fails.
    sealed class FetchesGarbage(string folder, string launcher) : ISessionFetcher
    {
        public string? Unavailable => null;

        public Task<FetchedBundle> FetchAsync(LaunchTransfer transfer, string peer, long maxBytes, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(folder);
            var zip = Path.Combine(folder, "bundle.zip");
            File.WriteAllText(zip, "not a zip");
            var about = new TransferResult
            {
                Blob = new BlobRef { Id = "b", Key = new string('A', 43) },
                Size = 9,
                Sha256 = new string('A', 43),
                Agent = AgentKind.Copilot,
                Launcher = launcher,
                Title = "Away",
                Files = 1,
            };
            return Task.FromResult(new FetchedBundle(zip, about, transfer.From));
        }
    }

    static LaunchRequest Transferring(Guid? request = null) => new()
    {
        Request = (request ?? Guid.NewGuid()).ToString(),
        Agent = "copilot",
        Workspace = "App",
        Transfer = new LaunchTransfer { From = new string('a', 32), Session = "99999999-9999-4999-8999-999999999999", Command = new SignedCommand { Body = "e30", By = "AA", Mac = "AA" } },
    };

    string LoggedAbout(string what) => Assert.Single(logged, line => line.Contains(what));

    [Fact]
    public async Task A_launch_that_fails_in_a_way_nobody_expected_says_the_machine_could_not_start_it_and_logs_the_exception()
    {
        var launcher = Launcher(Config(), fetcher: new FetchThrows(new InvalidOperationException(Recognisable)));

        var result = await launcher.LaunchAsync(Transferring(), "phone");

        Assert.Equal((LaunchOutcome.Refused, "the machine couldn't start the session"), (result.Outcome, result.Reason));
        Assert.Contains(Recognisable, LoggedAbout("failed before the agent was started"));
    }

    [Fact]
    public async Task A_session_the_other_machine_refuses_to_hand_over_says_it_could_not_be_brought_and_logs_the_refusal()
    {
        var launcher = Launcher(Config(), fetcher: new FetchThrows(new TransferRefusedException(Recognisable)));

        var result = await launcher.LaunchAsync(Transferring(), "phone");

        Assert.Equal((LaunchOutcome.Refused, "the session couldn't be brought from the other machine"), (result.Outcome, result.Reason));
        Assert.Contains(Recognisable, LoggedAbout("could not be brought from"));
    }

    [Fact]
    public async Task A_bundle_that_cannot_be_installed_says_so_in_fixed_words_and_logs_why()
    {
        var launcher = Launcher(Config(), fetcher: new FetchesGarbage(Path.Combine(f.Root, "garbage"), "copilot"));

        var result = await launcher.LaunchAsync(Transferring(), "phone");

        Assert.Equal((LaunchOutcome.Refused, "the session from the other machine couldn't be installed here; nothing was started"), (result.Outcome, result.Reason));
        var detail = LoggedAbout("could not be installed here: ").Split("could not be installed here: ")[1];
        Assert.NotEmpty(detail);
        Assert.DoesNotContain(detail, result.Reason);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task An_agent_that_cannot_be_started_says_so_in_fixed_words_and_logs_the_exception()
    {
        starter.Throws = new IOException(Recognisable);
        var launcher = Launcher(Config());

        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

        Assert.Equal((LaunchOutcome.Failed, "Copilot couldn't be started"), (result.Outcome, result.Reason));
        Assert.Contains(Recognisable, LoggedAbout("could not be started"));
    }

    [Fact]
    public async Task Attachments_that_cannot_be_staged_say_so_in_fixed_words_and_log_why()
    {
        using var store = new AttachmentStore(new AttachmentOptions { StagingRoot = Path.Combine(f.Root, "attachments") }, sweep: false);
        var launcher = Launcher(Config(), attachments: store);
        // Named a picture, but not one: staging refuses it.
        byte[] notAPng = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        var file = Guid.NewGuid().ToString();
        store.Start("phone", new() { Attachment = file, Name = "shot.png", Size = notAPng.Length, Sha256 = Attachments.Base64Url(System.Security.Cryptography.SHA256.HashData(notAPng)) });
        store.Chunk("phone", new() { Attachment = file, Offset = 0, Data = Convert.ToBase64String(notAPng) });

        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Attachments = [new() { Id = file }] }, "phone");

        Assert.Equal((LaunchOutcome.Refused, "the machine couldn't stage the attachments privately; nothing was started"), (result.Outcome, result.Reason));
        var detail = LoggedAbout("could not be staged privately: ").Split("could not be staged privately: ")[1];
        Assert.NotEmpty(detail);
        Assert.DoesNotContain(detail, result.Reason);
        Assert.Empty(starter.Specs);
    }
}
