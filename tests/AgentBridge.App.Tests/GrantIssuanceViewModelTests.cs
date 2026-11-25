using System.Collections.Immutable;
using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class GrantIssuanceViewModelTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 21, 21, 0, 0, TimeSpan.Zero);
    static GrantIssuanceScope Scope => new(["receiver"], ["repo"], ["copilot"],
        ["gpt-6.1-sol"], ["default"], ["medium"], ["default"], ["ask", "allowAll"]);
    static GrantIssuanceDraft Draft => new("account", "receiver", "default",
        [new("account", "source", "f9e0a0f8-018c-41ac-9957-688c79feeb6d", "credential-id", 123, Now)],
        ["launch"], Scope, new(10, 20, 8), Now, Now.AddHours(8));
    static GrantIssuancePolicy Policy => new("account", "receiver", "default",
        Scope, new(3, 10, 6), true, true, true);
    static GrantIssuanceViewModel Model(GrantIssuanceDraft? draft = null, GrantIssuancePolicy? policy = null) =>
        new(draft ?? Draft, policy ?? Policy, () => Now);

    [Fact]
    public void Confirmation_is_required_and_never_means_the_grant_is_active()
    {
        var vm = Model();
        Assert.True(vm.CanConfirm);
        Assert.False(vm.IsPersonConfirmed);
        Assert.False(vm.CanSubmit);
        Assert.Contains("not issued", vm.ConfirmationText);
        vm.ConfirmByPerson();
        Assert.True(vm.CanSubmit);
        Assert.Contains("host", vm.ConfirmationText);
    }

    [Fact]
    public void Effective_scope_is_exact_intersection_and_caps_can_only_narrow()
    {
        var scope = Scope with { Models = ["gpt-6.1-sol", "opus[1m]"], Workspaces = ["repo", "repo.child"] };
        var vm = Model(Draft with { Scope = scope });
        Assert.Equal(new GrantIssuanceCaps(3, 10, 6), vm.EffectiveCaps);
        Assert.Equal(["gpt-6.1-sol"], vm.EffectiveScope.Models.ToArray());
        Assert.Equal(["repo"], vm.EffectiveScope.Workspaces.ToArray());
        Assert.Contains("10/20/8", vm.Summary);
        Assert.Contains("3/10/6", vm.Summary);
        Assert.Contains("repo.child", vm.Summary);
        Assert.Contains(Draft.Subjects[0].Session, vm.Summary);
        Assert.DoesNotContain("credential-id", vm.Summary);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Allow_all_requires_both_explicit_grant_flag_and_host_policy(bool grant, bool host, bool effective)
    {
        var vm = Model(Draft with { AllowAllLaunch = grant }, Policy with { AllowAllLaunch = host });
        Assert.Equal(effective, vm.EffectiveAllowAllLaunch);
        Assert.Equal(effective, vm.EffectiveScope.Permissions.Contains("allowAll"));
        Assert.Contains($"requested {(grant ? "ON" : "OFF")}", vm.AllowAllLaunchText);
        Assert.Contains($"effective {(effective ? "ON" : "OFF")}", vm.AllowAllLaunchText);
    }

    [Fact]
    public void Changing_draft_or_policy_discards_the_previous_person_confirmation()
    {
        var vm = Model();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.ConfirmByPerson();
        vm.Update(Draft with { Caps = new(2, 4, 1) }, Policy);
        Assert.False(vm.CanSubmit);
        vm.ConfirmByPerson();
        vm.Update(vm.Draft, Policy with { LaunchAllowed = false });
        Assert.False(vm.IsPersonConfirmed);
        Assert.False(vm.CanConfirm);
        Assert.Contains("unavailable", vm.ValidationMessage);
        Assert.Contains(string.Empty, changed);
    }

    [Fact]
    public void Empty_selections_and_unbound_subjects_do_not_default_to_authority()
    {
        GrantIssuanceDraft[] invalid =
        [
            Draft with { Subjects = [] }, Draft with { Actions = [] },
            Draft with { Caps = new(0, 10, 1) },
            Draft with { Scope = Scope with { Models = ImmutableArray<string>.Empty } },
            Draft with { Subjects = [Draft.Subjects[0] with { ProcessId = 0 }] },
            Draft with { Subjects = [Draft.Subjects[0] with { Account = "other" }] },
            Draft with { Scope = Scope with { Machines = ["*"] } },
            Draft with { Actions = ["trust"] }, Draft with { Actions = default },
        ];
        foreach (var draft in invalid)
        {
            var vm = Model(draft);
            Assert.False(vm.CanConfirm);
            Assert.NotEmpty(vm.ValidationMessage);
            Assert.Throws<InvalidOperationException>(vm.ConfirmByPerson);
            Assert.False(vm.CanSubmit);
            Assert.NotEmpty(vm.Summary);
        }
    }

    [Fact]
    public void Stale_wrong_audience_or_expired_policy_cannot_be_confirmed()
    {
        Assert.False(Model(policy: Policy with { IsCurrent = false }).CanConfirm);
        Assert.False(Model(policy: Policy with { Device = "another" }).CanConfirm);
        Assert.False(Model(Draft with { ExpiresAt = Now }).CanConfirm);
        Assert.False(Model(Draft with { ExpiresAt = Now.AddHours(25) }).CanConfirm);
        Assert.False(Model(policy: Policy with { Caps = new(11, 10, 1) }).CanConfirm);
    }

    [Fact]
    public void Stop_only_is_not_blocked_by_removed_launch_tuning()
    {
        var vm = Model(Draft with { Actions = ["stop"] }, Policy with
        {
            LaunchAllowed = false,
            Scope = Scope with { Models = [] }
        });
        Assert.True(vm.CanConfirm);
        Assert.Equal(["stop"], vm.EffectiveActions.ToArray());
    }

    [Fact]
    public void Expiry_after_confirmation_prevents_submission_without_a_host_call()
    {
        var now = Now;
        var vm = new GrantIssuanceViewModel(Draft, Policy, () => now);
        vm.ConfirmByPerson();
        now = Now.AddHours(8);
        Assert.False(vm.CanSubmit);
        Assert.NotEmpty(vm.ValidationMessage);
    }
}
