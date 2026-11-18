using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public sealed record LaunchFolderConsent(string Host, string Baseline, string Draft)
{
    public string[] Folders => Draft.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string Consent => "Replace the saved approved launch folders with:\n"
        + (Folders.Length == 0 ? "(none: remove all folder approvals)" : string.Join("\n", Folders))
        + "\nRetained folders keep their metadata. Nothing restarts, accepts trust or launches. "
        + "The running host stays unchanged until you explicitly restart it.";
}

public sealed partial class SettingsViewModel
{
    LaunchFolderSnapshot? launchFolders, latestLaunchFolders;
    string? launchFoldersHost, latestLaunchFoldersHost;
    string launchFoldersDraft = "", launchFoldersNote = "";
    bool launchFoldersStale;

    public string LaunchFoldersDraft
    {
        get => launchFoldersDraft;
        set { if (Set(ref launchFoldersDraft, value)) { RaiseAll(); } }
    }
    bool HasLaunchFolderDraft => launchFolders is not null && LaunchFoldersDraft != string.Join(Environment.NewLine, launchFolders.Folders);
    bool LaunchFoldersStale => launchFoldersStale || (launchFolders is not null && launchFoldersHost != host.HostInstance);
    public bool CanReadLaunchFolders => HostRunning && host.Can(HostOps.LaunchFolders) && !Busy;
    public bool CanEditLaunchFolderDraft => CanReadLaunchFolders && launchFolders is not null && launchFoldersHost == host.HostInstance;
    public bool CanSaveLaunchFolders => CanEditLaunchFolderDraft && HasLaunchFolderDraft && !LaunchFoldersStale;
    public bool CanCancelLaunchFolderDraft => !Busy && latestLaunchFolders is not null && (HasLaunchFolderDraft || LaunchFoldersStale);
    public string LaunchFoldersState => !HostRunning ? "Connect to this machine's host to edit approved folders."
        : !host.Can(HostOps.LaunchFolders) ? "This host does not support folder editing. Update it before editing."
        : launchFolders is null ? "Load saved folders before drafting changes."
        : LaunchFoldersStale ? "The saved configuration or host changed. Your draft is kept but cannot save. Check saved folders, then cancel the draft to use them."
        : launchFoldersNote.Length > 0 ? launchFoldersNote : "One exact absolute folder per line. Add or remove lines to draft changes; review and confirm before saving.";

    async Task<LaunchFolderSnapshot> ReadLaunchFoldersAsync(string instance)
    {
        if (!HostRunning || !host.Can(HostOps.LaunchFolders)) { throw new InvalidOperationException(LaunchFoldersState); }
        var snapshot = Received.Read<LaunchFolderSnapshot>(await host.CallAsync(HostOps.LaunchFolders, new LaunchFolderRequest(),
            TimeSpan.FromSeconds(15), knownHost: true, onlyTo: instance, out _));
        if (host.HostInstance != instance) { throw new HostRestartedException(); }
        latestLaunchFolders = snapshot;
        latestLaunchFoldersHost = instance;
        return snapshot;
    }

    public Task LoadLaunchFoldersAsync() => RunAsync(async () =>
    {
        var instance = host.HostInstance ?? throw new InvalidOperationException("The host instance is not known yet.");
        var snapshot = await ReadLaunchFoldersAsync(instance);
        if (launchFolders is null || (!HasLaunchFolderDraft && !LaunchFoldersStale)) { UseLaunchFolders(snapshot, instance); }
        else { launchFoldersStale |= snapshot.Baseline != launchFolders.Baseline || launchFoldersHost != instance; }
    });

    void UseLaunchFolders(LaunchFolderSnapshot snapshot, string instance)
    {
        launchFolders = latestLaunchFolders = snapshot;
        launchFoldersHost = latestLaunchFoldersHost = instance;
        launchFoldersDraft = string.Join(Environment.NewLine, snapshot.Folders);
        launchFoldersStale = false;
        launchFoldersNote = "";
        RaiseAll();
    }

    public void CancelLaunchFolderDraft()
    {
        if (latestLaunchFolders is { } snapshot && latestLaunchFoldersHost is { } instance) { UseLaunchFolders(snapshot, instance); }
    }

    public LaunchFolderConsent CaptureLaunchFolderSave() => CanSaveLaunchFolders
        ? new(launchFoldersHost!, launchFolders!.Baseline, LaunchFoldersDraft)
        : throw new InvalidOperationException(LaunchFoldersState);

    public Task SaveLaunchFoldersAsync(LaunchFolderConsent consent) => RunAsync(async () =>
    {
        if (LaunchFoldersStale || consent.Host != host.HostInstance || consent.Host != launchFoldersHost
            || consent.Baseline != launchFolders?.Baseline || consent.Draft != LaunchFoldersDraft)
        { throw new InvalidOperationException("The confirmed folder draft or host changed. Review it again; nothing was saved."); }
        var snapshot = await ReadLaunchFoldersAsync(consent.Host);
        if (snapshot.Baseline != consent.Baseline)
        {
            launchFoldersStale = true;
            throw new InvalidOperationException("The folder draft is stale. It was kept, but nothing was saved.");
        }
        var saved = Received.Read<LaunchFolderSnapshot>(await host.CallAsync(HostOps.LaunchFolders,
            new LaunchFolderRequest(consent.Baseline, consent.Folders, Confirmed: true),
            TimeSpan.FromSeconds(15), knownHost: true, onlyTo: consent.Host, out _));
        if (host.HostInstance != consent.Host) { throw new HostRestartedException(); }
        UseLaunchFolders(saved, consent.Host);
        launchFoldersNote = "Saved. The running host is unchanged; restart it yourself when ready to apply these folders.";
    });
}
