using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentBridge.Host.Migration;

// Whether a scheduled task's security descriptor lets a user change it, read as Windows'
// AccessCheck does: the first allow or deny entry naming each right decides it.
//
// agent-ha-bridge registered from an administrator prompt leaves its task owned by Administrators,
// with the user granted read only (FR). Unelevated, the user's Administrators membership is
// deny-only, so Task Scheduler refuses to disable that task. A migration found that out only once
// it had started, and its undo then failed the same way on every rollback, so the profile could
// neither migrate nor roll back (#114). Asking first refuses before anything changes.
[SupportedOSPlatform("windows")]
public static class TaskAccess
{
    // Task Scheduler checks a task's descriptor as a file's; changing one needs write.
    const int FileWriteData = 0x2;
    const int GenericAll = 0x10000000, GenericExecute = 0x20000000, GenericWrite = 0x40000000, GenericRead = unchecked((int)0x80000000);
    const int FileAllAccess = 0x1F01FF, FileGenericRead = 0x120089, FileGenericWrite = 0x120116, FileGenericExecute = 0x1200A0;

    // Null when the descriptor cannot be read as one.
    public static bool? CanModify(string sddl, SecurityIdentifier user, IEnumerable<SecurityIdentifier> groups)
    {
        RawSecurityDescriptor descriptor;
        try { descriptor = new RawSecurityDescriptor(sddl); }
        catch (Exception e) when (e is ArgumentException or FormatException) { return null; }
        // No discretionary list at all grants everyone everything.
        if (descriptor.DiscretionaryAcl is not { } acl) { return true; }
        var who = new HashSet<SecurityIdentifier>(groups) { user };
        int granted = 0, refused = 0;
        foreach (var entry in acl)
        {
            if (entry is not CommonAce ace || ace.AceFlags.HasFlag(AceFlags.InheritOnly) || !who.Contains(ace.SecurityIdentifier)) { continue; }
            var mask = Map(ace.AccessMask);
            if (ace.AceQualifier == AceQualifier.AccessDenied) { refused |= mask & ~granted; }
            else if (ace.AceQualifier == AceQualifier.AccessAllowed) { granted |= mask & ~refused; }
        }
        return (granted & FileWriteData) != 0;
    }

    // The current user's answer, from the groups enabled in this process's token (an unelevated
    // token's Administrators membership is deny-only, so it is not among them).
    public static bool? CanModify(string sddl)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user) { return null; }
        var groups = identity.Groups?.OfType<SecurityIdentifier>() ?? [];
        return CanModify(sddl, user, groups);
    }

    static int Map(int mask)
    {
        var mapped = mask & ~(GenericAll | GenericExecute | GenericWrite | GenericRead);
        if ((mask & GenericAll) != 0) { mapped |= FileAllAccess; }
        if ((mask & GenericRead) != 0) { mapped |= FileGenericRead; }
        if ((mask & GenericWrite) != 0) { mapped |= FileGenericWrite; }
        if ((mask & GenericExecute) != 0) { mapped |= FileGenericExecute; }
        return mapped;
    }
}
