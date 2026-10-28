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
            var k = JsonDocument.Parse(File.ReadAllText(keyFile)).RootElement;
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
        var account = File.Exists(accountFile) ? JsonSerializer.Deserialize<SignInResult>(File.ReadAllText(accountFile)) : null;
        return new Identity(folder, key, storage, account);
    }

    // The account token is a bearer credential for the relay, so it is as private as the key.
    public void Save(SignInResult account)
    {
        Account = account;
        WritePrivate(Path.Combine(Folder, "account.json"), JsonSerializer.Serialize(account));
    }

    static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // Off Windows the file is created owner-only, rather than made and then narrowed, so
    // there is no moment at which another user can open it. %LOCALAPPDATA% is already the
    // user's own.
    static void WritePrivate(string path, string text)
    {
        if (OperatingSystem.IsWindows()) { File.WriteAllText(path, text); return; }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = ownerOnly }))
        using (var w = new StreamWriter(fs)) { w.Write(text); }
        File.SetUnixFileMode(path, ownerOnly);
    }
}
