using System.Globalization;
using AgentBridge.Roster;

namespace AgentBridge.App.Core;

public enum ApproveState { WaitingForCode1, Code2Shown, ReadyToApprove, CannotApprove, Approved, Ended, Locked }

public enum ApproveResult { NotReady, CannotApprove, Failed, Approved }

// What the approver signs: this exchange's transcript, the (possibly edited) name, and the proof that a person is present.
public sealed record ApprovalRequest(byte[] Transcript, string Name, IPresenceProof Proof);

// What a pending device says about itself, as the approver was told it. Shown as given, never as a name the app trusts.
public sealed record PendingDevice(string Name, string Kind, string Platform, string Id, DateTimeOffset SignedInAt);

public static class ApproveTexts
{
    public const string CannotApprove = "This device cannot approve yet.";

    public static string KindText(string? kind, string? platform) => (kind, platform) switch
    {
        ("app", "windows") => "a Windows app",
        ("app", _) => "an app",
        ("pwa", _) => "a browser app",
        ("phone", _) => "a phone",
        ("host", _) => "a host",
        _ => "a device of an unknown kind",
    };

    public static string Ago(TimeSpan age) => age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h ago");
}

// The approving device A (section 4.1 steps 2 and 4). The person types code 1 (never compares and taps), may edit the name, and only then
// sees code 2. Approving needs a presence proof; without one this ends in CannotApprove and never asks the proof or signs.
public sealed class ApproveViewModel : Observable
{
    readonly EnrollmentApprover approver;
    readonly IPresenceProof proof;
    readonly Func<ApprovalRequest, bool> sign;
    readonly TimeProvider time;
    ApproveState state;
    string name, statusText = "";

    public ApproveViewModel(EnrollmentApprover approver, PendingDevice pending, IPresenceProof proof, Func<ApprovalRequest, bool> sign, TimeProvider? time = null)
    {
        this.approver = approver;
        Pending = pending;
        this.proof = proof;
        this.sign = sign;
        this.time = time ?? TimeProvider.System;
        name = pending.Name;
    }

    public PendingDevice Pending { get; }
    public ApproveState State { get => state; private set => Set(ref state, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string Name { get => name; private set => Set(ref name, value); }
    public bool CanApprove => proof.Kind is Presence.Key or Presence.WebAuthn;
    public string IdShown => EnrollmentTexts.Groups(Pending.Id[..Math.Min(16, Pending.Id.Length)]);

    public string Banner => $"A new device wants to join: {Pending.Name}, says it is {ApproveTexts.KindText(Pending.Kind, Pending.Platform)}, id {IdShown}, "
        + $"signed in {ApproveTexts.Ago(time.GetUtcNow() - Pending.SignedInAt)}";

    // Code 2, for the person to type at the joining device: null until code 1 has matched.
    public string? Code2 => approver.Code2Shown;

    // The person may rename the device before approving: one to sixty-four characters, no control characters. False keeps the old name.
    public bool SetName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EnumerateRunes().Count() > EntryBody.MaxNameScalars || trimmed.Any(char.IsControl)) { return false; }
        Name = trimmed;
        return true;
    }

    public CodeResult EnterCode1(string input)
    {
        if (State != ApproveState.WaitingForCode1) { return CodeResult.Ended; }
        var result = approver.EnterCode1(input);
        StatusText = EnrollmentTexts.ForResult(result);
        State = result switch { CodeResult.Match => ApproveState.Code2Shown, CodeResult.Wrong => State, CodeResult.Locked => ApproveState.Locked, _ => ApproveState.Ended };
        return result;
    }

    // The joining device typed code 2 and said so (section 4.2 step 6).
    public void OnJoinerOk()
    {
        if (State == ApproveState.Code2Shown) { State = ApproveState.ReadyToApprove; }
    }

    public ApproveResult Approve()
    {
        if (!CanApprove) { State = ApproveState.CannotApprove; StatusText = ApproveTexts.CannotApprove; return ApproveResult.CannotApprove; }
        if (State != ApproveState.ReadyToApprove) { return ApproveResult.NotReady; }
        if (approver.TranscriptToSign is not { } transcript) { State = ApproveState.Ended; StatusText = EnrollmentTexts.Ended; return ApproveResult.Failed; }
        if (!sign(new ApprovalRequest(transcript, Name, proof))) { StatusText = EnrollmentTexts.Generic; return ApproveResult.Failed; }
        State = ApproveState.Approved;
        StatusText = "";
        return ApproveResult.Approved;
    }
}
