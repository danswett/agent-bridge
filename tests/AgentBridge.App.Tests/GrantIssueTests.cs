using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.App.Core.Grants;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class GrantIssueTests
{
    static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    static FakeGrantReceiver Receiver()
    {
        var receiver = new FakeGrantReceiver();
        var document = JsonSerializer.Serialize(new
        {
            id = Id, generation = 1, account = "123", issuer = new { account = "123", device = receiver.Receiver },
            audience = new { device = receiver.Receiver, profile = "default" },
            caps = new { hourly = 2, daily = 8, live = 4 },
            subjects = new[] { new { account = "123", sourceHost = receiver.Receiver,
                session = Id, credential = "AAAAAAAAAAAAAAAAAAAAAA", processInstance = new { pid = 42, startedAt = "2026-11-21T20:00:00Z" } } },
            actions = new[] { "launch", "stop" }, expiresAt = "2026-11-21T23:00:00Z",
            scope = new { machines = new[] { receiver.Receiver }, workspaces = new[] { "repo" },
                agents = new[] { "copilot" }, models = new[] { "small" }, profiles = new[] { "default" },
                efforts = new[] { "high" }, contexts = new[] { "default" }, permissions = new[] { "ask" } },
            roles = Array.Empty<string>(), stopTargets = Array.Empty<string>(), allowAllLaunch = false
        });
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
        receiver.Prepared = new(new(Id, 1, receiver.Receiver, "default", document, digest,
            new(2, 8, 4), "Current receiver limits: 2/hour, 8/day, 4 live; ask only.", []), null);
        receiver.Issued = new(new(Id, 1, receiver.Receiver, digest, false), null);
        return receiver;
    }
    static Task Prepare(GrantIssueViewModel vm) => vm.PrepareAsync(JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public async Task Issuance_is_refused_without_exact_person_confirmation()
    {
        var receiver = Receiver(); var vm = new GrantIssueViewModel(receiver);
        await Prepare(vm);
        await vm.ConfirmAsync("");
        Assert.Equal(0, receiver.Confirmations);
        Assert.False(vm.Active);
        Assert.Equal("person-required", vm.Problem);
    }

    [Fact]
    public async Task Submission_and_a_pending_response_never_show_Active()
    {
        var receiver = Receiver(); receiver.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
        var sending = vm.ConfirmAsync(vm.Digest);
        Assert.False(vm.Active);
        Assert.Equal("Issuing", vm.State);
        receiver.Pending.SetResult(receiver.Issued);
        await sending;
        Assert.True(vm.Active);
    }

    [Theory]
    [InlineData("RotationRequired")]
    [InlineData("rotation-required")]
    public async Task RotationRequired_is_shown_and_cannot_be_confirmed(string problem)
    {
        var receiver = Receiver(); receiver.Prepared = new(null, problem);
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
        Assert.Equal("Rotation required", vm.State);
        Assert.Equal(problem, vm.Problem);
        await vm.ConfirmAsync("ignored");
        Assert.Equal(0, receiver.Confirmations);
        Assert.False(vm.Active);
    }

    [Fact]
    public async Task The_displayed_document_digest_is_the_digest_sent_for_person_confirmation()
    {
        var receiver = Receiver(); var vm = new GrantIssueViewModel(receiver);
        await Prepare(vm);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(vm.Document))), vm.Digest);
        Assert.Contains("ask only", vm.EffectivePolicy);
        Assert.Contains("subjects: account: 123", vm.Summary);
        Assert.Contains("allowAllLaunch: false", vm.Summary);
        Assert.Equal(new(2, 8, 4), vm.Allocation);
        await vm.ConfirmAsync("wrong"); Assert.False(vm.Active);
        await vm.ConfirmAsync(vm.Digest);
        Assert.Equal(vm.Digest, receiver.ConfirmedDigest);
        Assert.True(vm.Active);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("generation")]
    [InlineData("receiver")]
    [InlineData("digest")]
    public async Task An_unmatched_receiver_receipt_never_shows_Active(string field)
    {
        var receiver = Receiver(); var receipt = receiver.Issued.Receipt!;
        receiver.Issued = new(field switch
        {
            "id" => receipt with { Id = Guid.NewGuid() },
            "generation" => receipt with { Generation = 2 },
            "receiver" => receipt with { Receiver = new('2', 32) },
            _ => receipt with { Digest = new('0', 64) }
        }, null);
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm); await vm.ConfirmAsync(vm.Digest);
        Assert.False(vm.Active);
        Assert.Equal("receipt-mismatch", vm.Problem);
    }

    [Fact]
    public async Task A_changed_document_or_an_unavailable_action_cannot_reach_confirmation()
    {
        foreach (var tampered in new[] { true, false })
        {
            var receiver = Receiver(); var preview = receiver.Prepared.Preview!;
            receiver.Prepared = new(tampered ? preview with { Document = preview.Document + " " }
                : preview with { UnavailableActions = ["stop"] }, null);
            var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
            await vm.ConfirmAsync("wrong"); await vm.ConfirmAsync(preview.Digest);
            Assert.False(vm.Active);
            Assert.Equal(0, receiver.Confirmations);
            Assert.Equal(tampered ? "digest-mismatch" : "action-unavailable", vm.Problem);
        }
    }

    [Theory]
    [InlineData("agent-ancestry")]
    [InlineData("script-ancestry")]
    public async Task A_person_path_refusal_prevents_the_app_from_requesting_signing(string problem)
    {
        var receiver = Receiver(); receiver.Problem = problem;
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm); await vm.ConfirmAsync("ignored");
        Assert.Equal(problem, vm.Problem);
        Assert.Equal(0, receiver.Confirmations);
        Assert.False(vm.Active);
    }

    [Fact]
    public async Task An_uncertain_submission_is_not_reissued_or_reprepared_automatically()
    {
        var receiver = Receiver(); receiver.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
        var sending = vm.ConfirmAsync(vm.Digest);
        receiver.Pending.SetException(new IOException());
        await sending; await vm.ConfirmAsync(vm.Digest); await Prepare(vm);
        Assert.False(vm.Active);
        Assert.Equal("Unconfirmed", vm.State);
        Assert.Equal(1, receiver.Confirmations);
    }

    [Fact]
    public async Task A_receiver_that_goes_offline_before_confirmation_is_explicitly_unavailable()
    {
        var receiver = Receiver(); var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
        receiver.Online = false; await vm.ConfirmAsync(vm.Digest);
        Assert.Equal("target-offline", vm.Problem);
        Assert.Equal(0, receiver.Confirmations);
        Assert.False(vm.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_and_receipt_authentication_failures_are_explicitly_Unconfirmed(bool authentication)
    {
        var receiver = Receiver(); receiver.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
        var sending = vm.ConfirmAsync(vm.Digest);
        receiver.Pending.SetException(authentication ? new CryptographicException() : new HttpRequestException());
        await sending;
        Assert.Equal("Unconfirmed", vm.State);
        Assert.Equal("unconfirmed", vm.Problem);
        Assert.False(vm.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_transport_or_authentication_failure_is_unavailable_without_signing(bool authentication)
    {
        var receiver = Receiver();
        receiver.PrepareError = authentication ? new CryptographicException() : new HttpRequestException();
        var vm = new GrantIssueViewModel(receiver); await Prepare(vm);
        Assert.Equal("unavailable", vm.Problem);
        Assert.False(vm.CanConfirm);
        Assert.Equal(0, receiver.Confirmations);
    }
}
