using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using R4Proof;

// R4Proof <agent-bridge-injector.exe> <results.md>, run by Run-R4Proof.ps1 (README.md).
const string RpId = "r4-presence-proof.invalid";
if (args.Length != 2) { Console.Error.WriteLine("usage: R4Proof <agent-bridge-injector.exe> <results.md>"); return 3; }
// The app runs unelevated; an elevated harness would prove nothing about same-user code beside it.
if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.Error.WriteLine("Run this from a normal, unelevated PowerShell.");
    return 3;
}
if (!Hello.PlatformAvailable)
{
    Console.Error.WriteLine($"Windows Hello is not available to webauthn.dll here (API version {Hello.ApiVersion}); nothing to prove.");
    return 3;
}

var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")!;
string[] environment =
[
    $"Machine: {Environment.MachineName}; run {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}",
    $"OS: {nt.GetValue("ProductName")} {nt.GetValue("DisplayVersion")}, build {nt.GetValue("CurrentBuild")}.{nt.GetValue("UBR")}",
    $"Harness: unelevated, integrity {Integrity()}",
    $"webauthn.dll API version {Hello.ApiVersion}; user-verifying platform authenticator available",
    $"Console injector: src/AgentBridge.Injector (agent-bridge-injector), built by Run-R4Proof.ps1",
];

var stopping = false;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping = true;
    Console.WriteLine("Ctrl+C: stopping after this step, then deleting the test passkey. Press Cancel on any Windows Security prompt.");
};

var report = Runner.Run(new Hello(RpId, Attacks.ForegroundWindow()), RpId, Attacks.All(args[0]), Attacks.Arm, () => stopping, Console.WriteLine, TimeSpan.FromSeconds(4));
File.WriteAllText(args[1], report.Markdown(environment));
Console.WriteLine();
foreach (var s in report.Steps)
{
    Console.WriteLine($"{s.Outcome switch { Outcome.Blocked => "PASS (blocked)", Outcome.Failed => "FAIL", _ => "INCONCLUSIVE" },-15} {s.Name}: {s.Raw}");
}
Console.WriteLine($"{report.Cleanup}\nVerdict: {report.Verdict}. Send this file back: {args[1]}");
return report.ExitCode;

static string Integrity()
{
    using var p = Process.Start(new ProcessStartInfo("whoami", "/groups /fo csv /nh") { RedirectStandardOutput = true })!;
    var line = p.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault(l => l.Contains(@"Mandatory Label\"));
    return line?.Split(',')[0].Trim('"').Replace(@"Mandatory Label\", "") ?? "unknown";
}
