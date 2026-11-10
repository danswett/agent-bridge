namespace AgentBridge.Host;

// Unattended start of a VM (decision D3, #1595, #1623): the dedicated host account's password
// kept as an LSA secret so Windows can sign it in after a reboot, and the way to take that away.
// A person runs it at the machine, elevated; it is off until they do (#801), and nothing the
// relay sends can run it. The password is asked for, never an argument, is stored only as the
// LSA "DefaultPassword" secret (never the plaintext Winlogon value) and is never printed or sent.
// The account must not be an administrator, and is refused remote and network logon.
public interface IUnattendedSystem
{
    bool IsElevated { get; }
    // Why this machine's policy stops Windows signing in by itself, or null when it does not.
    string? AutoLogonBlockedReason();
    bool IsAdministrator(string account);
    // A null password removes the secret.
    void StorePassword(string? password);
    string? GetWinlogon(string name);
    void SetWinlogon(string name, string value);
    void DeleteWinlogon(string name);
    void AddRight(string account, string right);
    void RemoveRight(string account, string right);
}

public static class UnattendedCommands
{
    public const string Usage = "agent-bridge-host setup-unattended --user <[DOMAIN\\]account> | disable-unattended [--user <[DOMAIN\\]account>]";

    public const string Interactive = "SeInteractiveLogonRight";
    public static readonly string[] Denied = ["SeDenyNetworkLogonRight", "SeDenyRemoteInteractiveLogonRight"];

    // Everything setup may leave in Winlogon, plus a hand-made plaintext DefaultPassword, so none survives a revoke.
    static readonly string[] WinlogonValues = ["AutoAdminLogon", "DefaultUserName", "DefaultDomainName", "DefaultPassword", "AutoLogonCount", "ForceAutoLogon"];

    public static int Run(IReadOnlyList<string> args, IUnattendedSystem system, Func<string?> readPassword, TextWriter output, TextWriter error)
    {
        var user = Option(args, "--user");
        try
        {
            return args.ElementAtOrDefault(0) switch
            {
                "setup-unattended" => Setup(user, system, readPassword, output, error),
                "disable-unattended" => Disable(user, system, output, error),
                _ => UsageError(error),
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Nothing here puts the password in an exception, so the message is safe to show.
            error.WriteLine($"agent-bridge-host: unattended start was not changed completely: {ex.Message}. Run disable-unattended to take it all away.");
            return 1;
        }
    }

    static int Setup(string? user, IUnattendedSystem system, Func<string?> readPassword, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(user)) { return UsageError(error); }
        if (!system.IsElevated) { return Fail(error, "setup needs an elevated terminal, once, to store the password for Windows."); }
        if (system.AutoLogonBlockedReason() is { } blocked) { return Fail(error, $"this machine cannot sign in by itself, so nothing was changed: {blocked}."); }
        if (system.IsAdministrator(user)) { return Fail(error, $"{user} is an administrator. Unattended start uses a dedicated standard account; nothing was changed."); }
        var (domain, name) = Split(user);
        error.Write($"Password for {user} (not shown, kept only in Windows' secret store): ");
        var password = readPassword();
        error.WriteLine();
        if (string.IsNullOrEmpty(password)) { return Fail(error, "no password was given; nothing was changed."); }

        // Rights first and the switch last: a failure part-way never leaves Windows trying to sign in without a secret.
        system.AddRight(user, Interactive);
        foreach (var right in Denied) { system.AddRight(user, right); }
        system.DeleteWinlogon("DefaultPassword");
        system.StorePassword(password);
        system.SetWinlogon("DefaultUserName", name);
        system.SetWinlogon("DefaultDomainName", domain);
        system.SetWinlogon("AutoAdminLogon", "1");
        output.WriteLine($"Unattended start is on for {user}: the next restart signs in as that account by itself. `agent-bridge-host disable-unattended` takes it away.");
        return 0;
    }

    // Removing what is already gone is not an error, so running it again finishes the job.
    static int Disable(string? user, IUnattendedSystem system, TextWriter output, TextWriter error)
    {
        if (!system.IsElevated) { return Fail(error, "disable-unattended needs an elevated terminal to remove the secret and the auto-logon values."); }
        var current = system.GetWinlogon("DefaultUserName");
        var domain = system.GetWinlogon("DefaultDomainName");
        var account = user ?? (string.IsNullOrEmpty(current) ? null : string.IsNullOrEmpty(domain) ? current : $"{domain}\\{current}");

        // Switch off first. The user and domain values go last, so a failed RemoveRight can be rerun without --user.
        system.SetWinlogon("AutoAdminLogon", "0");
        system.StorePassword(null);
        foreach (var value in WinlogonValues.Where(v => v is not ("DefaultUserName" or "DefaultDomainName"))) { system.DeleteWinlogon(value); }
        if (account is not null)
        {
            system.RemoveRight(account, Interactive);
            foreach (var right in Denied) { system.RemoveRight(account, right); }
        }
        system.DeleteWinlogon("DefaultUserName");
        system.DeleteWinlogon("DefaultDomainName");
        output.WriteLine(account is null
            ? "Unattended start is off: the secret and the auto-logon values are removed. No account was named or recorded, so its logon rights are untouched; run again with --user to remove them."
            : $"Unattended start is off for {account}: the secret, the auto-logon values and the logon rights this tool set are removed. The next restart stops at the normal sign-in.");
        return 0;
    }

    static (string Domain, string Name) Split(string user) =>
        user.Split('\\', 2) is [var d, var n] ? (d, n) : (Environment.MachineName, user);

    static int Fail(TextWriter error, string message) { error.WriteLine($"agent-bridge-host: {message}"); return 1; }

    static int UsageError(TextWriter error) { error.WriteLine($"usage: {Usage}"); return 2; }

    static string? Option(IReadOnlyList<string> args, string name)
    {
        var i = args.ToList().IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
