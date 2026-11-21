using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Roster;

namespace R4Proof;

public enum Outcome { Blocked, Failed, Inconclusive }
public enum Conclusion { Held, Failed, Inconclusive }

public sealed record Call(int Hr, Assertion? Result);
public sealed record Created(int Hr, byte[] CredentialId, byte[] Key, bool BackupEligible, bool UserVerified, string Format);
public sealed record Prompt(nint Hwnd, int Pid, string Describe);
public sealed record Step(string Name, string How, Outcome Outcome, string Raw);

// NeedsPrompt: the attack acts on the prompt's window, so it waits to find it and sends nothing if
// it cannot. EndingMeansDelivered: the attack only presses Esc, Cancel or the like, so a prompt that
// ends while it runs proves injected input reaches it (the harness never knows the PIN, and a wrong
// PIN counts toward the TPM's lockout, so delivery is what it tests).
public sealed record Attack(string Name, string How, bool NeedsPrompt, bool EndingMeansDelivered, bool RequireUv,
    Func<Prompt?, (bool Sent, string Note)> Run);

public interface IHello
{
    Created Create(byte[] challenge);
    Call AssertWithPerson(byte[] challenge);
    (Task<Call> Done, Action Cancel) Begin(byte[] challenge, bool requireUv);
    int Delete(byte[] credentialId);
}

public sealed class Report
{
    public List<string> Setup { get; } = [];
    public List<Step> Steps { get; } = [];
    public string Cleanup { get; set; } = "";
    public bool Qualifies { get; set; }

    public Conclusion Verdict =>
        Steps.Any(s => s.Outcome == Outcome.Failed) ? Conclusion.Failed
        : Qualifies && Steps.Count > 0 && Steps.All(s => s.Outcome == Outcome.Blocked) && !Cleanup.Contains("stopped") ? Conclusion.Held
        : Conclusion.Inconclusive;

    public int ExitCode => Verdict switch { Conclusion.Held => 0, Conclusion.Failed => 1, _ => 2 };

    public string Markdown(IEnumerable<string> environment)
    {
        var md = new StringBuilder($"# R4 presence proof: {Verdict}\n\nWritten by `spikes/r4-presence/Run-R4Proof.ps1`. Blocked means the attack got no assertion and did not reach the prompt.\n\n## Environment and the person's steps\n\n");
        foreach (var line in environment.Concat(Setup)) { md.Append($"* {line}\n"); }
        md.Append("\n## Attacks\n\n| Attack | How | Outcome | Raw |\n|---|---|---|---|\n");
        foreach (var s in Steps) { md.Append($"| {s.Name} | {s.How} | {s.Outcome} | {s.Raw.Replace('|', '/')} |\n"); }
        md.Append($"\nCleanup: {Cleanup}\n");
        if (Verdict == Conclusion.Held) { md.Append(Claim); }
        return md.ToString();
    }

    const string Claim = """

        ## Claim

        On this machine and OS build, a Windows Hello platform credential made through webauthn.dll (ES256,
        user verification required, backup-eligible clear) gave no assertion to code running as the same
        user, unelevated, at the app's integrity level: SendInput, UI Automation and agent-bridge-injector
        did not reach its prompt, no assertion came back without the person, and a replayed assertion was
        refused by the roster verifier. Not covered: a device compromised before it enrolls, malware that
        waits for the person's own approval and changes what is approved (the confirmation screen can lie
        on a compromised approver, design §3.1 and §4.6), and code running elevated or as SYSTEM.

        """;
}

public static class Runner
{
    static readonly BigInteger N = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber);

    public static Report Run(IHello hello, string rpId, IReadOnlyList<Attack> attacks, Func<Func<Prompt?>> arm,
        Func<bool> stop, Action<string> say, TimeSpan settle)
    {
        var report = new Report();
        say("Step 1 of 3: Windows Hello asks you to make a test passkey. Enter your PIN.");
        var seen = Task.Run(arm());
        var made = hello.Create(RandomNumberGenerator.GetBytes(32));
        report.Setup.Add($"Create: {Hr(made.Hr)}, attestation '{made.Format}', backup-eligible {made.BackupEligible}, user verified {made.UserVerified}; prompt {seen.Result?.Describe}");
        if (made.Hr != 0) { report.Cleanup = "no test passkey was made"; return report; }
        try
        {
            var credential = new WebAuthnCredential(rpId, made.CredentialId, made.Key, made.Format is "tpm" or "packed", made.BackupEligible);
            say("Step 2 of 3: Windows Hello asks again. Enter your PIN.");
            seen = Task.Run(arm());
            var challenge = RandomNumberGenerator.GetBytes(32);
            var mine = hello.AssertWithPerson(challenge);
            var check = mine.Result is { } a ? Verify(a, credential, challenge) : "no assertion";
            report.Setup.Add($"Person's assertion: {Hr(mine.Hr)}, flags 0x{mine.Result?.AuthenticatorData[32]:x2}, high-S {mine.Result is { } h && !LowS(h.Signature).SequenceEqual(h.Signature)}, roster verifier: {check}; prompt {seen.Result?.Describe}");
            report.Qualifies = !made.BackupEligible && made.UserVerified && check == "accepted";
            var replay = mine.Result is { } m ? Verify(m, credential, RandomNumberGenerator.GetBytes(32)) : null;
            report.Steps.Add(new("replay", "the person's assertion, checked by the roster verifier against a new challenge",
                replay is null ? Outcome.Inconclusive : replay == "accepted" ? Outcome.Failed : Outcome.Blocked, replay ?? "no assertion to replay"));
            say("Step 3 of 3: hands off the keyboard and mouse until it says done. Do not answer any prompt.");
            Thread.Sleep(settle);
            foreach (var attack in attacks)
            {
                if (stop()) { report.Cleanup = "stopped by Ctrl+C; "; break; }
                say($"  {attack.Name}");
                report.Steps.Add(Probe(hello, attack, arm, settle));
            }
        }
        finally
        {
            string deleted;
            try { deleted = Hr(hello.Delete(made.CredentialId)); }
            catch (Exception e) { deleted = e.GetType().Name; }
            report.Cleanup += $"test passkey deleted: {deleted}" + (deleted == "ok" ? "" : $" (remove {rpId} in Settings, Accounts, Passkeys)");
        }
        return report;
    }

    static Step Probe(IHello hello, Attack a, Func<Func<Prompt?>> arm, TimeSpan settle)
    {
        var find = arm();
        var (done, cancel) = hello.Begin(RandomNumberGenerator.GetBytes(32), a.RequireUv);
        var raw = "";
        try
        {
            var prompt = find();
            raw = $"prompt: {prompt?.Describe ?? "none"}; ";
            if (a.NeedsPrompt && prompt is not { Hwnd: not 0 }) { return new(a.Name, a.How, Outcome.Inconclusive, raw + "nothing sent"); }
            var (sent, note) = a.Run(prompt);
            raw += note;
            if (!done.Wait(settle)) { return new(a.Name, a.How, sent ? Outcome.Blocked : Outcome.Inconclusive, raw + "; prompt still waiting for a person"); }
            var call = done.Result;
            raw += $"; call ended {Hr(call.Hr)}" + (call.Result is { } r ? $", flags 0x{r.AuthenticatorData[32]:x2}" : "");
            return new(a.Name, a.How, call.Hr == 0 || a.EndingMeansDelivered ? Outcome.Failed : Outcome.Blocked, raw);
        }
        catch (Exception e) { return new(a.Name, a.How, Outcome.Inconclusive, raw + $"threw {e.GetType().Name}: {e.Message}"); }
        finally
        {
            // The API's own cancel; if Windows ignores it, the call's 30 s timeout ends it.
            if (!done.IsCompleted) { cancel(); done.Wait(TimeSpan.FromSeconds(35)); }
        }
    }

    // Anyone can turn (r, s) into (r, n - s) without the key, so a client normalises what Hello
    // signs before the roster's low-S rule (P256.CheckRosterSignature) sees it.
    static byte[] LowS(byte[] der)
    {
        var seq = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        var (r, s) = (seq.ReadInteger(), seq.ReadInteger());
        return P256.Der(r, s > N / 2 ? N - s : s);
    }

    static string Verify(Assertion a, WebAuthnCredential c, byte[] challenge)
    {
        try { WebAuthnAssertion.Check(a with { Signature = LowS(a.Signature) }, c, challenge); return "accepted"; }
        catch (RosterFormatException e) { return e.Code; }
        catch (AsnContentException) { return "signature not DER"; }
    }

    static string Hr(int hr) => hr == 0 ? "ok" : $"0x{hr:x8}";
}