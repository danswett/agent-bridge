using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Claims;
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
    // What changing, deleting or replacing a file takes, and what replacing something in a folder takes (a script credential's pinned
    // files and the folders above them, docs/design/script-credentials.md section 6): adding a file or a subfolder replaces nothing.
    const int AddFile = 0x2, AppendData = 0x4, DeleteChild = 0x40, Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    const int ChangeFile = FileWriteData | AppendData | Delete | WriteDac | WriteOwner;
    const int ChangeFolder = DeleteChild | Delete | WriteDac | WriteOwner;
    const int GenericAll = 0x10000000, GenericExecute = 0x20000000, GenericWrite = 0x40000000, GenericRead = unchecked((int)0x80000000);
    const int FileAllAccess = 0x1F01FF, FileGenericRead = 0x120089, FileGenericWrite = 0x120116, FileGenericExecute = 0x1200A0;

    // Null when the descriptor cannot be read as one. Groups are those enabled in the token;
    // denyOnly are those it holds for denial alone (an unelevated administrator's
    // Administrators), which Windows matches against deny entries but never allow ones.
    public static bool? CanModify(string sddl, SecurityIdentifier user, IEnumerable<SecurityIdentifier> groups, IEnumerable<SecurityIdentifier>? denyOnly = null) =>
        Granted(sddl, user, groups, denyOnly, FileWriteData) is { } granted ? (granted & FileWriteData) != 0 : null;

    // True when the user can neither change, delete nor replace a file (folder: nor delete or re-permission a folder or its children),
    // false when it can, null when the descriptor cannot decide it. An owner may always change the permissions of what it owns
    // unless an OWNER RIGHTS entry says otherwise, so an object the user owns is never locked.
    // `beside`: the folder holds a pinned file, so adding a file or a subfolder to it also counts as changing it (a library planted beside a
    // pinned program runs in its place). A descriptor with no owner cannot say who may re-permission it, so it is undecided.
    public static bool? Unchangeable(string sddl, bool folder, SecurityIdentifier user, IEnumerable<SecurityIdentifier> groups, IEnumerable<SecurityIdentifier>? denyOnly = null, bool beside = false)
    {
        var wanted = folder ? ChangeFolder | (beside ? AddFile | AppendData : 0) : ChangeFile;
        return Granted(sddl, user, groups, denyOnly, wanted, needOwner: true) is { } granted ? (granted & wanted) == 0 : null;
    }

    public static bool? Unchangeable(string sddl, bool folder, bool beside = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user) { return null; }
        var denyOnly = identity.Claims.Where(c => c.Type == ClaimTypes.DenyOnlySid).Select(c => new SecurityIdentifier(c.Value)).ToList();
        return Unchangeable(sddl, folder, user, identity.Groups?.OfType<SecurityIdentifier>() ?? [], denyOnly, beside);
    }

    // The rights among `wanted` the descriptor grants, or null when an entry that could still decide one of them cannot be evaluated here.
    static int? Granted(string sddl, SecurityIdentifier user, IEnumerable<SecurityIdentifier> groups, IEnumerable<SecurityIdentifier>? denyOnly, int wanted, bool needOwner = false)
    {
        RawSecurityDescriptor descriptor;
        try { descriptor = new RawSecurityDescriptor(sddl); }
        catch (Exception e) when (e is ArgumentException or FormatException) { return null; }
        if (needOwner && descriptor.Owner is null) { return null; }
        // No discretionary list at all grants everyone everything.
        if (descriptor.DiscretionaryAcl is not { } acl) { return wanted; }
        var who = new HashSet<SecurityIdentifier>(groups) { user };
        var denied = new HashSet<SecurityIdentifier>(who);
        denied.UnionWith(denyOnly ?? []);
        // OWNER RIGHTS entries apply to whoever owns the object, in place of the owner's implicit
        // rights; owning it grants no write of its own. An owner held for denial only meets its
        // denials alone, as any deny-only group does.
        var ownerRights = new SecurityIdentifier(WellKnownSidType.WinCreatorOwnerRightsSid, null);
        var owns = false;
        if (descriptor.Owner is { } owner)
        {
            owns = who.Contains(owner);
            if (who.Contains(owner)) { who.Add(ownerRights); }
            if (denied.Contains(owner)) { denied.Add(ownerRights); }
        }
        int granted = 0, refused = 0;
        foreach (var entry in acl)
        {
            // An entry of a kind this does not evaluate (a compound one, say) could grant or refuse: undecided, unless it applies to children only.
            if (entry is not CommonAce ace) { if (entry.AceFlags.HasFlag(AceFlags.InheritOnly)) { continue; } return null; }
            if (ace.AceFlags.HasFlag(AceFlags.InheritOnly)) { continue; }
            var mask = Map(ace.AccessMask);
            var applies = ace.AceQualifier == AceQualifier.AccessDenied ? denied.Contains(ace.SecurityIdentifier)
                : ace.AceQualifier == AceQualifier.AccessAllowed && who.Contains(ace.SecurityIdentifier);
            // An OWNER RIGHTS entry in a descriptor read without its owner may or may not apply.
            var ownerUnknown = descriptor.Owner is null && ace.SecurityIdentifier == ownerRights;
            // A conditional entry (XA, XD) counts only when its expression holds for the token,
            // which is not evaluated here. Either one that could still decide write leaves it
            // unknown, so the scheduler decides instead: a false yes or no would mislead the preflight.
            if (((applies && ace.IsCallback) || ownerUnknown) && (mask & wanted & ~(granted | refused)) != 0) { return null; }
            if (!applies || ace.IsCallback || ownerUnknown) { continue; }
            if (ace.AceQualifier == AceQualifier.AccessDenied) { refused |= mask & ~granted; }
            else { granted |= mask & ~refused; }
        }
        // An owner holds WRITE_DAC implicitly while no OWNER RIGHTS entry names the right.
        if (owns && !acl.OfType<CommonAce>().Any(a => a.SecurityIdentifier == ownerRights)) { granted |= WriteDac & ~refused; }
        return granted & wanted;
    }

    // The current user's answer, from this process's token: Groups holds only the enabled
    // groups, and the deny-only ones come as DenyOnlySid claims.
    public static bool? CanModify(string sddl)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user) { return null; }
        var groups = identity.Groups?.OfType<SecurityIdentifier>() ?? [];
        var denyOnly = identity.Claims.Where(c => c.Type == ClaimTypes.DenyOnlySid).Select(c => new SecurityIdentifier(c.Value)).ToList();
        return CanModify(sddl, user, groups, denyOnly);
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
