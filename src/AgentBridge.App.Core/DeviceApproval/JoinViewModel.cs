using AgentBridge.Roster;

namespace AgentBridge.App.Core;

public enum JoinState { NotStarted, Locked, WaitingForApprover, EnterCode2, Joined, Ended }

// The joining device N (section 4.1 steps 1 and 3, section 4.2): it shows code 1, then what it is joining, and the person types code 2.
public sealed class JoinViewModel(EnrollmentJoiner joiner) : Observable
{
    JoinState state;
    string code1 = "", fingerprint = "", statusText = "";

    public JoinState State { get => state; private set => Set(ref state, value); }
    public string Code1 { get => code1; private set => Set(ref code1, value); }
    public string Fingerprint { get => fingerprint; private set => Set(ref fingerprint, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    // The commitment to send first; null while joining is locked (three ended attempts in an hour).
    public byte[]? Commit()
    {
        var commit = joiner.Commit();
        if (commit is null) { State = JoinState.Locked; StatusText = EnrollmentTexts.Locked; return null; }
        State = JoinState.WaitingForApprover;
        return commit;
    }

    // The approver's offer: the reveal to send back, and code 1 to show. Null when the offer cannot be taken.
    public byte[]? OnOffer(byte[] nonceA)
    {
        var reveal = joiner.OnOffer(nonceA);
        if (reveal is null) { return null; }
        Code1 = joiner.Code1Shown ?? "";
        return reveal;
    }

    // The roster it would join arrived (its genesis hash). The code 2 box appears only after code 1 is shown.
    public void ShowGenesis(byte[] genesisHash)
    {
        if (State != JoinState.WaitingForApprover || Code1.Length == 0) { return; }
        Fingerprint = EnrollmentTexts.Fingerprint(genesisHash);
        State = JoinState.EnterCode2;
    }

    // What the person typed here from the approver's screen.
    public CodeResult Submit(string input)
    {
        if (State != JoinState.EnterCode2) { return CodeResult.Ended; }
        var result = joiner.EnterCode2(input);
        StatusText = EnrollmentTexts.ForResult(result);
        State = result switch { CodeResult.Match => JoinState.Joined, CodeResult.Wrong => State, CodeResult.Locked => JoinState.Locked, _ => JoinState.Ended };
        return result;
    }
}
