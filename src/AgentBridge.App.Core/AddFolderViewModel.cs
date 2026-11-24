using System.Globalization;
using System.Text.Json;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

public enum AddFolderState { Idle, Confirming, Adding, Added, Refused }

public enum ConfirmChoice { Cancel, Approve }

// The fixed words of Add folder: no text from the host is ever shown, only what its closed code stands for here.
public static class AddFolderTexts
{
    public const string Generic = "This folder could not be approved.";
    public const string Restart = "Approved. It takes effect after the host restarts.";
    public const string Approved = "Approved.";
    public const string Unshowable = "That folder path cannot be shown safely, so it was not offered.";
    public const string Unconfirmed = "The host did not answer, so it is not known whether this folder was approved. Check the approved folders before trying again.";

    static readonly Dictionary<string, string> Refusals = new(StringComparer.Ordinal)
    {
        ["not-a-path"] = "That is not a folder path.",
        ["missing"] = "That folder does not exist.",
        ["system-folder"] = "A system folder cannot be approved.",
        ["already-approved"] = "That folder is already approved.",
        ["label-in-use"] = "Another approved folder already has that name.",
        ["too-many"] = "The limit of 64 approved folders is reached.",
        ["unwritable"] = "The host could not save its settings.",
        ["config-unreadable"] = "The host's settings file could not be read.",
        ["not-local"] = "Only folders on this computer's own drives can be approved.",
        ["busy"] = "The host is busy saving its settings. Try again in a moment.",
    };

    public static string ForCode(string? code) => code is not null && Refusals.TryGetValue(code, out var words) ? words : Generic;
}

// Add folder in Settings. The person picks a folder, sees its full path and what approving means, and only Approve asks the host
// (host.workspace.add), which makes the real check and writes nothing without it. Cancel is the default choice. The folder approved is the
// exact one chosen, never a parent. Call it from UI handlers only: its properties are raised on the caller's context, as SettingsViewModel's are.
public sealed class AddFolderViewModel(HostConnection host, Func<Task<string?>> pickFolder) : Observable
{
    const int MaxShownPath = 1024;
    AddFolderState state;
    string? pendingPath;
    string statusText = "";
    bool picking, sending;

    public bool Available => host.Can(HostOps.WorkspacesCapability);
    public ConfirmChoice DefaultChoice => ConfirmChoice.Cancel;
    public AddFolderState State { get => state; private set => Set(ref state, value); }
    public string? PendingPath { get => pendingPath; private set { Set(ref pendingPath, value); Raise(nameof(ConfirmText)); } }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    public string ConfirmText => PendingPath is { } path ? $"Approve {path}? Agents started there can read and change files in it." : "";

    // The picker, then the confirmation; nothing is sent. A cancelled picker leaves everything as it was, and one picker is open at a time.
    public async Task ChooseAsync()
    {
        if (!Available || picking || State is AddFolderState.Confirming or AddFolderState.Adding) { return; }
        picking = true;
        string? path;
        try { path = await pickFolder(); } finally { picking = false; }
        if (path is not { Length: > 0 }) { return; }
        PendingPath = null;
        // What the person is asked to approve must read as it is: a line break, a direction override or a very long path could hide the warning.
        if (path.Length > MaxShownPath || path.Any(Hidden)) { StatusText = AddFolderTexts.Unshowable; State = AddFolderState.Refused; return; }
        StatusText = "";
        PendingPath = path;
        State = AddFolderState.Confirming;
    }

    static bool Hidden(char c) => char.IsControl(c)
        || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse;

    public void Cancel()
    {
        if (State != AddFolderState.Confirming) { return; }
        PendingPath = null;
        State = AddFolderState.Idle;
    }

    public async Task ApproveAsync()
    {
        if (State != AddFolderState.Confirming || PendingPath is not { } path || sending || !Available) { return; }
        sending = true;
        State = AddFolderState.Adding;
        var next = AddFolderState.Refused;
        var words = AddFolderTexts.Generic;
        try
        {
            var answer = await host.CallAsync(HostOps.WorkspaceAdd, new { path });
            if (answer.TryGetProperty("result", out var result) && result.GetString() == "added")
            {
                next = AddFolderState.Added;
                words = answer.TryGetProperty("restartRequired", out var restart) && restart.ValueKind == JsonValueKind.True ? AddFolderTexts.Restart : AddFolderTexts.Approved;
            }
            else { words = AddFolderTexts.ForCode(answer.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null); }
        }
        catch (HostErrorException) { } // the host answered with an error: nothing was approved
        catch (Exception) { words = AddFolderTexts.Unconfirmed; } // the connection or the wait failed: the host may have written it
        finally
        {
            sending = false;
            PendingPath = null;
            StatusText = words;
            State = next;
        }
    }
}
