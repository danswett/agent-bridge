using System.Text.Json;

namespace AgentBridge.Transport;

// A profile folder: the device key, and the account it signed in to. On Windows the key is
// kept in the TPM, or with DPAPI without one (spike S6); elsewhere, until P5 brings the
// Keychain and Secure Enclave, in a key.json only its owner can read.
//
// The folder is the product's own: %LOCALAPPDATA%\AgentBridge\<profile> on Windows,
// ~/.agent-bridge/<profile> elsewhere. Never agent-ha-bridge's, which may be running
// alongside on the same machine.
public sealed class Identity
{
    public string Folder { get; }
    public P256KeyPair Key { get; }
    public KeyStorage Storage { get; }
    public SignInResult? Account { get; private set; }

    Identity(string folder, P256KeyPair key, KeyStorage storage, SignInResult? account) { Folder = folder; Key = key; Storage = storage; Account = account; }

    public static string DefaultRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");

    // A profile is a folder name, never a path: "../x" must not reach outside the root.
    public static bool IsValidProfile(string? profile) =>
        profile is { Length: > 0 and <= 64 } && profile is not ("." or "..")
        && profile.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static Identity Load(string profile, KeyStorage? prefer = null, string? root = null)
    {
        if (!IsValidProfile(profile)) { throw new ArgumentException($"\"{profile}\" is not a profile name: use letters, digits, '-', '_' and '.'", nameof(profile)); }
        root ??= DefaultRoot;
        CreatePrivateDirectory(root);
        var folder = Path.Combine(root, profile);
        CreatePrivateDirectory(folder);
        var keyFile = Path.Combine(folder, "key.json");
        P256KeyPair key;
        KeyStorage storage;
        if (File.Exists(keyFile))
        {
            using var document = JsonDocument.Parse(ReadPrivate(keyFile));
            var k = document.RootElement;
            key = P256KeyPair.FromParameters(Wire.Unhex(k.GetProperty("d").GetString()!), Wire.Unhex(k.GetProperty("x").GetString()!), Wire.Unhex(k.GetProperty("y").GetString()!));
            storage = KeyStorage.File;
        }
        // Asked for by name, TPM or DPAPI goes through DeviceKeys, which refuses a platform
        // without them; only an unspecified choice off Windows falls back to a plain file.
        else if (DeviceKeys.Exists(folder) || prefer is KeyStorage.Tpm or KeyStorage.Dpapi || (prefer is null && OperatingSystem.IsWindows()))
        {
            var dk = DeviceKeys.LoadOrCreate(folder, prefer);
            (key, storage) = (dk.Key, dk.Storage);
        }
        else
        {
            key = P256KeyPair.Generate();
            var (d, x, y) = key.Export();
            WritePrivate(keyFile, JsonSerializer.Serialize(new { d = Wire.Hex(d), x = Wire.Hex(x), y = Wire.Hex(y) }));
            storage = KeyStorage.File;
        }
        var accountFile = Path.Combine(folder, "account.json");
        var account = File.Exists(accountFile) ? JsonSerializer.Deserialize<SignInResult>(ReadPrivate(accountFile)) : null;
        return new Identity(folder, key, storage, account);
    }

    // The account token is a bearer credential for the relay, so it is as private as the key.
    public void Save(SignInResult account)
    {
        WritePrivate(Path.Combine(Folder, "account.json"), JsonSerializer.Serialize(account));
        Account = account;
    }

    // Reads account.json again, for a long-running host: another process (the CLI's sign-in)
    // may have signed this profile in since. True when the account changed.
    public bool ReloadAccount()
    {
        SignInResult? now;
        try
        {
            var file = Path.Combine(Folder, "account.json");
            now = File.Exists(file) ? JsonSerializer.Deserialize<SignInResult>(ReadPrivate(file)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
        if (now is null || now == Account) { return false; }
        Account = now;
        return true;
    }

    static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, ownerOnly);
            if (new DirectoryInfo(path).LinkTarget is not null) { throw new IOException("An identity directory cannot be a symbolic link."); }
            // CreateDirectory's mode only applies to a new directory. A pre-existing
            // development root must be private before any credentials are read or made.
            File.SetUnixFileMode(path, ownerOnly);
        }
    }

    static string ReadPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (new FileInfo(path).LinkTarget is not null) { throw new IOException("An identity credential cannot be a symbolic link."); }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return File.ReadAllText(path);
    }

    // Off Windows the file is created owner-only, rather than made and then narrowed, so
    // there is no moment at which another user can open it. %LOCALAPPDATA% is already the
    // user's own.
    static void WritePrivate(string path, string text)
    {
        if (OperatingSystem.IsWindows()) { File.WriteAllText(path, text); return; }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        if (new FileInfo(path).LinkTarget is not null) { throw new IOException("An identity credential cannot be a symbolic link."); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = ownerOnly }))
            {
                using (var writer = new StreamWriter(fs, leaveOpen: true)) { writer.Write(text); }
                fs.Flush(flushToDisk: true);
            }
            // Readers see the old or new token whole, and no existing file or link is
            // opened for writing (which could overwrite a different file).
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
