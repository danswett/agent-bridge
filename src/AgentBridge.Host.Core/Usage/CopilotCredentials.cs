using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentBridge.Host.Core.Usage;

// Reads a stored credential by name, or null. Windows' Credential Manager in the product;
// a fake in tests, so nothing there ever reaches a real store.
public interface ICredentialStore
{
    string? Read(string target);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    public string? Read(string target)
    {
        if (!CredReadW(target, 1, 0, out var handle)) { return null; }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) { return null; }
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            // agent-ha-bridge reads the blob as UTF-16; a store written by a Node keychain
            // module holds UTF-8. Whichever reads as a token is the one.
            var wide = blob.Length % 2 == 0 ? Encoding.Unicode.GetString(blob) : null;
            if (wide is not null && CopilotCredentials.LooksLikeToken(wide)) { return wide; }
            var narrow = Encoding.UTF8.GetString(blob);
            return CopilotCredentials.LooksLikeToken(narrow) ? narrow : null;
        }
        finally { CredFree(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}

// The token Copilot CLI itself would spend, so the quota read is the same account's: the
// ladder the CLI documents, COPILOT_GITHUB_TOKEN, GH_TOKEN, GITHUB_TOKEN, its own stored
// login (Windows), then `gh auth token` for the same login. Read, used for one request and
// dropped: never logged, never sent anywhere but api.github.com, never written.
//
// macOS's keychain item is not read: reading it raises an authorization panel that the CLI's
// next token refresh invalidates, so the prompt cannot be made to stop by answering it
// (agent-ha-bridge #122). agent-ha-bridge reads it only when configured to; this host has no
// configuration yet, so it never does.
public sealed class CopilotCredentials(UsageOptions options, ICommandRunner commands, ICredentialStore? store = null,
    Func<string, string?>? environment = null, Func<string?>? findGh = null, TimeProvider? time = null, Action<string>? log = null)
{
    readonly Func<string, string?> env = environment ?? Environment.GetEnvironmentVariable;
    readonly Func<string?> gh = findGh ?? FindGh;
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly Action<string> say = log ?? (_ => { });
    readonly Dictionary<string, string> reasons = new(StringComparer.Ordinal);
    readonly object sync = new();
    // A gh that did not answer is held off for every login alike: a keyring holding it at a
    // prompt holds it whoever it is asked for.
    DateTimeOffset? heldSince;

    public static bool LooksLikeToken(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= 4096 && text.All(c => c is >= '\x21' and <= '\x7e');

    public async Task<string?> ResolveAsync(string login, string hostUrl, CancellationToken cancellationToken = default)
    {
        // The quota is read from api.github.com, and a token for any other host is not one to
        // send there.
        if (!IsGitHub(hostUrl) || string.IsNullOrWhiteSpace(login)) { return null; }
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var value = env(name)?.Trim();
            if (LooksLikeToken(value)) { return value; }
        }
        if (store is not null)
        {
            // Stored under the account it belongs to, so a machine signed in to two accounts
            // hands back this one's. A store that cannot be read leaves gh to try.
            try
            {
                var stored = store.Read($"{hostUrl.TrimEnd('/')}:{login}.copilot-cli")?.Trim();
                if (LooksLikeToken(stored)) { return stored; }
            }
            catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        }
        return await FromGhAsync(login, hostUrl, cancellationToken);
    }

    // Why gh gave no token for this login, for the reading's error; null when it was not asked.
    public string? GhReason(string login, string hostUrl)
    {
        lock (sync) { return reasons.TryGetValue(Key(login, hostUrl), out var r) ? r : null; }
    }

    // Somebody has just opened a Copilot session here, so a keyring that held gh up may well be
    // open now. Never within GhPresent of the timeout: sessions opening in quick succession
    // must not become the prompt storm the hold exists to prevent.
    public bool ReleaseHold(DateTimeOffset now)
    {
        lock (sync)
        {
            if (heldSince is not { } since || now - since < options.GhPresent) { return false; }
            heldSince = null;
            return true;
        }
    }

    async Task<string?> FromGhAsync(string login, string hostUrl, CancellationToken cancellationToken)
    {
        var key = Key(login, hostUrl);
        string reason;
        bool held;
        lock (sync)
        {
            held = heldSince is { } since && clock.GetUtcNow() - since < options.GhHold;
            if (!held) { heldSince = null; }
        }
        if (held) { reason = "gh did not answer in time"; }
        else if (gh() is not { } program) { reason = "gh is not installed"; }
        else
        {
            var result = await commands.RunAsync(program, ["auth", "token", "--hostname", new Uri(hostUrl).Host, "--user", login], options.GhTimeout, cancellationToken);
            if (result.TimedOut)
            {
                lock (sync) { heldSince = clock.GetUtcNow(); }
                reason = "gh did not answer in time";
            }
            else if (!result.Ran) { reason = "gh could not be run"; }
            else if (result.ExitCode != 0) { reason = "gh has no token for this account; run gh auth login"; }
            else
            {
                // Standard output alone, and exactly one line of printable characters: it goes
                // into an Authorization header, and anything else gh printed is not part of it.
                var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 1 && LooksLikeToken(lines[0]))
                {
                    lock (sync) { reasons.Remove(key); }
                    return lines[0];
                }
                reason = "gh answered with something that is not a token";
            }
        }
        // Said when it changes, not every poll. What gh printed is never logged (on success it
        // is the token), and neither is the login.
        bool changed;
        lock (sync)
        {
            changed = !reasons.TryGetValue(key, out var previous) || previous != reason;
            reasons[key] = reason;
        }
        if (changed) { say($"usage: the Copilot allowance could not be read through gh: {reason}"); }
        return null;
    }

    static string Key(string login, string hostUrl) => $"{hostUrl}:{login.ToLowerInvariant()}";

    static bool IsGitHub(string hostUrl) =>
        Uri.TryCreate(hostUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    // On PATH, or where gh's installers put it: a service or scheduled task can start the host
    // with a PATH from before gh was installed.
    static string? FindGh()
    {
        var name = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) { return candidate; }
            }
            catch (ArgumentException) { }
        }
        IEnumerable<string> known = OperatingSystem.IsWindows()
            ? new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } l ? Path.Combine(l, "Programs") : null }
                .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "GitHub CLI", "gh.exe"))
            : ["/opt/homebrew/bin/gh", "/usr/local/bin/gh", "/opt/local/bin/gh", "/usr/bin/gh", "/home/linuxbrew/.linuxbrew/bin/gh",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "gh")];
        return known.FirstOrDefault(File.Exists);
    }
}
