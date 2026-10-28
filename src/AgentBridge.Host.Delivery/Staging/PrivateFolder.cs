using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentBridge.Host.Delivery.Staging;

// A folder only this user can open, for files on their way to an agent. agent-ha-bridge's
// Protect-BridgeSecretFile, as it narrows its attachment root: on Windows a protected DACL
// (nothing inherited from %LOCALAPPDATA% or above) granting this user alone full control,
// inherited by everything inside, and the user as owner; elsewhere mode 0700, with each file
// created 0600. A folder that cannot be made so is never written to.
public static class PrivateFolder
{
    // Creates the folder if need be, narrows it if it is not private, and returns its full
    // path. Throws if it is a link or stays readable by anyone else.
    public static string Ensure(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(full); }
        else { Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        // A link would put the files wherever it points, under that place's permissions.
        if (new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("the attachment folder is a link, so nothing is staged in it");
        }
        if (!IsPrivate(full))
        {
            Narrow(full);
            if (!IsPrivate(full)) { throw new IOException("the attachment folder could not be made private to this user, so nothing is staged in it"); }
        }
        return full;
    }

    public static bool IsPrivate(string path)
    {
        if (OperatingSystem.IsWindows()) { return IsPrivateWindows(path); }
        return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
    }

    // Never over an existing file: names are random, so one already there is not ours.
    public static void WriteNew(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }

    static void Narrow(string path)
    {
        if (OperatingSystem.IsWindows()) { NarrowWindows(path); return; }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    static void NarrowWindows(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(me);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    static bool IsPrivateWindows(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected || !me.Equals(security.GetOwner(typeof(SecurityIdentifier)))) { return false; }
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var mine = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (!me.Equals(rule.IdentityReference)) { return false; }
            mine |= rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl);
        }
        return mine;
    }
}
