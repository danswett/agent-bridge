using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentBridge.App;

static partial class SelfTest
{
    const string PostedQuarantineKey = "quarantined PostMessage input 2471 2476 2483 until 2026-11-20";
    static readonly DateTimeOffset PostedQuarantineExpiry = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
    sealed record PostedCanary(bool Eligible, bool Ready, bool Posted, bool Received, string Detail);
    sealed record PostedQuarantine(string Status, int[] Issues, string Owner, string Accountability, string Expires, List<object> Checks);

    static async Task CheckPostedInput(MainWindow window, Action<bool, string> check, Dictionary<string, object?> result)
    {
        var job = Environment.GetEnvironmentVariable("GITHUB_JOB");
        var architecture = RuntimeInformation.ProcessArchitecture;
        var ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        var eligible = ci && job == "app-installer" && architecture == Architecture.Arm64;
        if (!ci)
        {
            result["postedInputCanary"] = new { eligible, job, architecture = architecture.ToString() };
            return;
        }
        await CaptureFrame(window.RootElement);
        var focused = FocusManager.GetFocusedElement(window.RootElement.XamlRoot);
        var focusState = focused is Control { FocusState: not FocusState.Unfocused } element ? element.FocusState : FocusState.Programmatic;
        var selected = window.Sessions.Rows.SelectedItem;
        var (target, inWindow, detail) = PostedInputTarget(window);
        detail = $"job: {job ?? "unset"}; process: {architecture}; {detail}";
        var ready = focused is not null && inWindow;
        var received = false;
        var posted = false;
        var heard = new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.F24) { received = true; e.Handled = true; }
        });
        window.RootElement.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
        try
        {
            posted = ready && PostKey(target, VirtualKey.F24, extended: false);
            if (posted) { await Until(() => received, TimeSpan.FromSeconds(3)); }
        }
        finally
        {
            window.RootElement.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
            if (focused is not null && !ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), focused))
            {
                var held = new Windows.Foundation.TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
                window.RootElement.BringIntoViewRequested += held;
                try
                {
                    var restored = (await FocusManager.TryFocusAsync(focused, focusState)).Succeeded
                        && ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), focused);
                    ready &= restored;
                    check(restored, $"restores focus after the independent F24 canary ({FocusedName(window)})");
                }
                finally { window.RootElement.BringIntoViewRequested -= held; }
            }
        }
        var selectionKept = ReferenceEquals(selected, window.Sessions.Rows.SelectedItem);
        ready &= selectionKept;
        if (!selectionKept) { check(false, "leaves selection unchanged after the independent F24 canary"); }
        result["postedInputCanary"] = new PostedCanary(eligible, ready, posted, received, detail);
        if (received) { check(true, "receives the independent posted F24 canary; input checks stay strict"); }
        else if (!eligible || !ready || !posted || DateTimeOffset.UtcNow >= PostedQuarantineExpiry)
        {
            check(false, $"posted F24 canary failed (ready: {ready}; posted: {posted}; received: {received}; {detail}; issue 2476; quarantine expires 2026-11-20T08:00:00Z)");
        }
    }

    static bool QuarantinePostedInput(Dictionary<string, object?> result, Action<bool, string> check, string name, object detail)
    {
        // #2476: only the ARM64 installer canary permits these checks to quarantine, until 2026-11-20 00:00 PT.
        if (result.GetValueOrDefault("postedInputCanary") is not PostedCanary { Eligible: true, Ready: true, Posted: true, Received: false }
            || DateTimeOffset.UtcNow >= PostedQuarantineExpiry) { return false; }
        if (result.GetValueOrDefault(PostedQuarantineKey) is not PostedQuarantine quarantine)
        {
            quarantine = new("quarantined", [2471, 2476, 2483], "SG Builder 1981 fix", "Engineering Lead accountable",
                "2026-11-20T08:00:00Z", []);
            result[PostedQuarantineKey] = quarantine;
            check(true, "posted F24 input not delivered on ARM64 installer (quarantined, issues 2471, 2476, 2483, expires 2026-11-20; owner SG Builder 1981 fix; Engineering Lead accountable; 2483 posts no input: Chief runner-cohort exception, not a proven delivery cause)");
        }
        quarantine.Checks.Add(new { name, detail });
        AppLog.Write($"self-test: quarantined: {name}: {System.Text.Json.JsonSerializer.Serialize(detail)}");
        return true;
    }

    static (nint Target, nint Focus) PostedTarget(nint window)
    {
        var focus = GetFocus();
        return (focus != 0 ? focus : window, focus);
    }

    static (nint Target, bool InWindow, string Detail) PostedInputTarget(MainWindow window)
    {
        var (target, focus) = PostedTarget(window.Handle);
        var root = GetAncestor(target, 2);
        var foreground = GetForegroundWindow();
        return (target, root == window.Handle,
            $"window: {window.Handle:X}; target: {target:X}; native focus: {focus:X}; target root: {root:X}; target in window: {root == window.Handle}; foreground: {foreground:X}; foreground matches: {foreground == window.Handle}; XAML focus: {FocusedName(window)}");
    }

    static bool PostKey(nint target, VirtualKey key, bool extended)
    {
        var scan = (ushort)MapVirtualKeyW((uint)key, 0);
        var lParam = 1 | ((nint)scan << 16) | (extended ? (nint)(1 << 24) : 0);
        var down = PostMessageW(target, 0x0100, (nint)(ushort)key, lParam);
        var up = PostMessageW(target, 0x0101, (nint)(ushort)key, lParam | (1 << 30) | unchecked((nint)(1u << 31)));
        return down && up;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);
}
