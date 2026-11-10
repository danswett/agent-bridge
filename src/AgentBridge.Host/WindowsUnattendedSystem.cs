using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace AgentBridge.Host;

// The real machine behind UnattendedCommands: LSA for the secret and the account rights,
// Winlogon's registry key for the auto-logon switch. Needs an elevated process.
[SupportedOSPlatform("windows")]
public sealed class WindowsUnattendedSystem : IUnattendedSystem
{
    const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string SecretName = "DefaultPassword"; // the name Winlogon looks the stored password up under
    const uint LookupNames = 0x800, CreateAccount = 0x10, CreateSecret = 0x20;
    const uint NotFound = 0xC0000034;

    public bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // Windows Hello-only sign-in and a legal notice at sign-in each stop Windows signing in by itself.
    public string? AutoLogonBlockedReason()
    {
        using var hello = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device");
        if (hello?.GetValue("DevicePasswordLessBuildVersion") is int and not 0) { return "Windows Hello-only sign-in is on (turn off \"Require Windows Hello sign-in for Microsoft accounts\")"; }
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (policy?.GetValue("LegalNoticeText") is string { Length: > 0 } || policy?.GetValue("LegalNoticeCaption") is string { Length: > 0 }) { return "a legal notice policy is set, which stops at the sign-in screen"; }
        return null;
    }

    public bool IsAdministrator(string account)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount)).Value;
        var adminGroup = admins[(admins.IndexOf('\\') + 1)..];
        var code = NetUserGetLocalGroups(null, account, 0, 1, out var buffer, -1, out var entries, out _);
        if (code != 0) { throw new Win32Exception(code, $"could not read the groups of {account}"); }
        try
        {
            for (var i = 0; i < entries; i++)
            {
                var group = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                if (string.Equals(group, adminGroup, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }
        finally { NetApiBufferFree(buffer); }
    }

    public void StorePassword(string? password)
    {
        using var policy = Open(CreateSecret);
        var key = Unicode(SecretName);
        var value = password is null ? default : Unicode(password);
        try
        {
            // A null value deletes the secret; one that is already gone is the state asked for.
            var status = password is null ? LsaStorePrivateData(policy.Handle, ref key, IntPtr.Zero) : LsaStorePrivateData(policy.Handle, ref key, ref value);
            Check(status, "storing the secret", allowNotFound: password is null);
        }
        finally { Free(key); Free(value); }
    }

    public string? GetWinlogon(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Winlogon);
        return key?.GetValue(name)?.ToString();
    }

    public void SetWinlogon(string name, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Winlogon, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteWinlogon(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Winlogon, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void AddRight(string account, string right) => Rights(account, right, add: true);
    public void RemoveRight(string account, string right) => Rights(account, right, add: false);

    static void Rights(string account, string right, bool add)
    {
        var sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new InvalidOperationException($"{account} is not an account on this machine");
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        using var policy = Open(LookupNames | CreateAccount);
        var rights = new[] { Unicode(right) };
        try
        {
            var status = add ? LsaAddAccountRights(policy.Handle, bytes, rights, 1) : LsaRemoveAccountRights(policy.Handle, bytes, false, rights, 1);
            Check(status, $"{(add ? "granting" : "removing")} {right}", allowNotFound: !add);
        }
        finally { Free(rights[0]); }
    }

    sealed class Policy(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => LsaClose(Handle);
    }

    static Policy Open(uint access)
    {
        var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, access, out var handle), "opening the security policy");
        return new Policy(handle);
    }

    static void Check(uint status, string what, bool allowNotFound = false)
    {
        if (status == 0 || (allowNotFound && status == NotFound)) { return; }
        throw new Win32Exception(LsaNtStatusToWinError(status), $"{what} failed");
    }

    static UnicodeString Unicode(string s) => new() { Length = (ushort)(s.Length * 2), MaximumLength = (ushort)(s.Length * 2 + 2), Buffer = Marshal.StringToHGlobalUni(s) };

    static void Free(UnicodeString s)
    {
        if (s.Buffer == IntPtr.Zero) { return; }
        // Zero the characters before freeing: this buffer held the password.
        for (var i = 0; i < s.Length; i++) { Marshal.WriteByte(s.Buffer, i, 0); }
        Marshal.FreeHGlobal(s.Buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr policy, ref UnicodeString key, ref UnicodeString value);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr policy, ref UnicodeString key, IntPtr value);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaRemoveAccountRights(IntPtr policy, byte[] sid, [MarshalAs(UnmanagedType.U1)] bool all, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buffer, int max, out int entries, out int total);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buffer);
}
