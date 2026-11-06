using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AgentBridge.App;

// The sign-in sheet (#800, ui.md §6.13): a dialog while MainViewModel.SignInSheet is set, from a
// person's Sign in on a banner. Cancel is where it starts and what Escape does, and closing it
// cancels the flow. It shows the URL as plain text with Open github.com, the code in mono with Copy
// code, and when it expires; Open opens only the allowlist's own string (AuthState), on its press.
// The code is never logged or announced: what is said aloud is the banner's words.
public sealed partial class SessionsPage
{
    ContentDialog? signInDialog;
    SignInSheetViewModel? signInShown;
    // The model closed the sheet (signed in, failed, its banner gone): not a person's Cancel.
    bool signInClosing;
    // The banner to put focus back on once the sheet has finished closing, by its key, and whether on
    // its status line (this close was a person here signing in).
    (string Key, bool Line)? signInFocusAfter;

    void WatchSignIns(MainViewModel vm)
    {
        vm.PropertyChanged += OnSignInSheet;
        vm.Announce += SayPolitely;
        vm.SignInSheetClosed += OnSignInSheetClosed;
        vm.SignInRedrawing += OnSignInRedrawing;
    }

    // A banner drawn anew takes a new container, so focus on its old one (its status line, just after
    // the sheet closed on success) would go nowhere: it goes to the new one's instead, once drawn.
    void OnSignInRedrawing(string key)
    {
        if (viewModel?.SignIns.FirstOrDefault(b => b.Key == key) is not { } old
            || SignInBannerList.ContainerFromItem(old) is not DependencyObject container
            || XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused) { return; }
        for (var up = focused; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (ReferenceEquals(up, container))
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusSignInBanner(key));
                return;
            }
        }
    }

    // The dialog puts focus back where it was when it opened (the banner's Sign in, perhaps gone by
    // now) as it finishes hiding, so while it is still there, focus waits for it to have gone.
    void OnSignInSheetClosed(string key, bool signedIn)
    {
        if (signInDialog is not null) { signInFocusAfter = (key, signedIn); return; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusSignInBanner(key, signedIn));
    }

    void OnSignInSheet(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.SignInSheet) or "" or null) || viewModel is null) { return; }
        var sheet = viewModel.SignInSheet;
        if (ReferenceEquals(sheet, signInShown)) { return; }
        if (signInDialog is { } open)
        {
            signInClosing = true;
            open.Hide();
        }
        if (sheet is not null) { ShowSignInSheet(sheet); }
    }

    async void ShowSignInSheet(SignInSheetViewModel sheet)
    {
        signInShown = sheet;
        signInFocusAfter = null;
        var starting = new TextBlock { Text = sheet.StartingText, TextWrapping = TextWrapping.Wrap };
        // Not GitHub's link, or not GitHub's kind of code: possibly phishing, so none of it is shown,
        // and nor is the person sent to the machine, which only the host's signIn: machine may claim.
        var refused = new TextBlock { Text = "This sign-in code couldn\u2019t be shown.", TextWrapping = TextWrapping.Wrap };
        var expired = new TextBlock { Text = "Code expired", TextWrapping = TextWrapping.Wrap };
        var where = new TextBlock { Text = "Open this page and enter the code.", TextWrapping = TextWrapping.Wrap };
        var pageText = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"], IsTextSelectionEnabled = true, FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(pageText, "SignInPage");
        // Selectable, and read a character at a time, as it is typed.
        var code = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"],
            FontSize = 28,
            IsTextSelectionEnabled = true,
            FlowDirection = FlowDirection.LeftToRight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(code, "SignInCode");
        var expiry = new TextBlock { Style = (Style)Application.Current.Resources["AbSecondaryText"] };
        var copy = new Button { Content = "Copy", MinHeight = 32 };
        AutomationProperties.SetAutomationId(copy, "SignInCopy");
        var openPage = new Button { Content = "Open", MinHeight = 32 };
        AutomationProperties.SetAutomationId(openPage, "SignInOpen");
        var copied = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(copied, AutomationLiveSetting.Polite);
        var flow = new StackPanel { Spacing = 8, Children = { where, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pageText, openPage } }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { code, copy } }, expiry, copied } };
        var body = new StackPanel { Spacing = 12, MinWidth = 320, Children = { starting, refused, expired, flow } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Content = body,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "SignInSheet");

        void Draw()
        {
            dialog.Title = sheet.Title;
            starting.Visibility = Ui.Show(sheet.Starting);
            refused.Visibility = Ui.Show(sheet.Refused);
            expired.Visibility = Ui.Show(sheet.Expired);
            flow.Visibility = Ui.Show(sheet.HasCode);
            starting.Text = sheet.StartingText;
            pageText.Text = sheet.Page;
            code.Text = sheet.Code;
            AutomationProperties.SetName(code, sheet.CodeName);
            expiry.Text = sheet.Expiry;
            AutomationProperties.SetName(copy, sheet.CopyName);
            AutomationProperties.SetName(openPage, sheet.OpenName);
            copied.Text = sheet.Copied;
            // Try again, for a code that couldn't be shown or has expired (ui.md §6.13): no text, no button.
            dialog.PrimaryButtonText = sheet.CanRetry ? "Try again" : "";
            if (FindIn<Button>(dialog, "CloseButton") is { } cancel) { AutomationProperties.SetName(cancel, sheet.CancelName); }
            if (FindIn<Button>(dialog, "PrimaryButton") is { } retry) { AutomationProperties.SetName(retry, sheet.RetryName); }
        }
        PropertyChangedEventHandler redraw = (_, _) => Draw();
        sheet.PropertyChanged += redraw;
        // Once for each Copy: a redraw (every sync) never says it again.
        Action<string> sayCopied = text =>
        {
            copied.Text = text;
            FrameworkElementAutomationPeer.CreatePeerForElement(copied)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
        sheet.CopiedSaid += sayCopied;
        copy.Click += (_, _) =>
        {
            if (!sheet.HasCode) { return; }
            try
            {
                var data = new DataPackage();
                data.SetText(sheet.Code);
                Clipboard.SetContent(data);
                sheet.CopiedCode(true);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { sheet.CopiedCode(false); }
        };
        openPage.Click += async (_, _) =>
        {
            // The allowlist's own string, on this press only.
            if (sheet.OpenUri is not { } uri) { return; }
            try { await Windows.System.Launcher.LaunchUriAsync(uri); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        };
        // Cancel is where it starts (§6.13), so Enter or Space can't open or copy anything unmeant.
        // Programmatic: WinUI draws the ring only when the last input was a key, never after a touch
        // or a click (§9.1 A12).
        dialog.Opened += (_, _) =>
        {
            Draw();
            FindIn<Button>(dialog, "CloseButton")?.Focus(FocusState.Programmatic);
        };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            // The sheet stays: the new request draws into it.
            e.Cancel = true;
            viewModel?.RetrySignIn();
        };
        dialog.Closing += (_, _) =>
        {
            if (signInClosing) { return; }
            // Cancel or Escape: the flow is cancelled at the machine too.
            if (ReferenceEquals(viewModel?.SignInSheet, sheet)) { viewModel!.CancelSignIn(); }
        };
        Draw();
        signInDialog = dialog;
        signInClosing = false;
        try { await dialog.ShowAsync(); }
        // Another dialog is open (only one can be): no sheet, so no flow left running unseen.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (ReferenceEquals(viewModel?.SignInSheet, sheet)) { viewModel!.CancelSignIn(); }
        }
        finally
        {
            sheet.PropertyChanged -= redraw;
            sheet.CopiedSaid -= sayCopied;
            if (ReferenceEquals(signInDialog, dialog)) { signInDialog = null; }
            if (ReferenceEquals(signInShown, sheet)) { signInShown = null; }
            signInClosing = false;
            if (signInDialog is null && signInFocusAfter is { } after)
            {
                signInFocusAfter = null;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusSignInBanner(after.Key, after.Line));
            }
        }
    }

    // Where focus goes once the sheet has gone (ui.md §6.13): the banner's button if it is still there
    // (Cancel, failure, expiry); on success its status line, "Signed in · waiting…"; and with the banner
    // gone, the session list's heading. The last two take focus programmatically only.
    // Not yet loaded, the target refuses focus: after the next layout pass the banner is looked up
    // again by its key, as it may have been drawn anew in between (the host's pending after
    // signin.done), so the retry is never on a container that has gone.
    void FocusSignInBanner(string key, bool line = false, bool again = false)
    {
        var banner = viewModel?.SignIns.FirstOrDefault(b => b.Key == key);
        // The banner's new state (its button gone, its status line shown) laid out before it is looked for.
        SignInBannerList.UpdateLayout();
        var container = banner is null ? null : SignInBannerList.ContainerFromItem(banner) as DependencyObject;
        var status = container is null ? null : FindIn<ContentControl>(container, null, "SignInStatus");
        // Closed by a person here signing in: the status line, even while Sign in is still drawn, as the
        // host's pending (which takes it away) may come after the sheet closed.
        var target = container is null ? null : line && status is not null ? status : (Control?)FindIn<Button>(container, null, "SignInVerb") ?? status;
        if (target is not null)
        {
            if (FocusNow(target)) { return; }
            if (!again)
            {
                void Again(object? sender, object e)
                {
                    SignInBannerList.LayoutUpdated -= Again;
                    FocusSignInBanner(key, line, again: true);
                }
                SignInBannerList.LayoutUpdated += Again;
                SignInBannerList.InvalidateArrange();
                return;
            }
        }
        FocusOnce(SessionsHeading);
    }

    // Focus on a control that is no Tab stop: it is one until focus leaves it. False, and left as it
    // was, if it refuses.
    static bool FocusNow(Control target)
    {
        var made = !target.IsTabStop;
        if (made) { target.IsTabStop = true; }
        if (!target.Focus(FocusState.Programmatic))
        {
            if (made) { target.IsTabStop = false; }
            return false;
        }
        void Done(object sender, RoutedEventArgs e) { target.LostFocus -= Done; target.IsTabStop = false; }
        if (made) { target.LostFocus += Done; }
        return true;
    }

    // Focus, once, on a control: not yet loaded, it refuses focus, so it is tried again once, after
    // the next layout pass.
    static void FocusOnce(Control target)
    {
        if (FocusNow(target)) { return; }
        void Again(object? sender, object e)
        {
            target.LayoutUpdated -= Again;
            FocusNow(target);
        }
        target.LayoutUpdated += Again;
        target.InvalidateArrange();
    }

    // What changed, said to a screen reader: a sign-in needed, or how it ended. Never the code.
    // From the window's root, which is always shown: this page is collapsed while Machines, Usage or
    // Settings is open, and a collapsed element's notifications may never reach a screen reader.
    internal UIElement SignInSpeaker => XamlRoot?.Content ?? this;

    void SayPolitely(string text) =>
        FrameworkElementAutomationPeer.CreatePeerForElement(SignInSpeaker)?.RaiseNotificationEvent(
            AutomationNotificationKind.Other, AutomationNotificationProcessing.All, text, $"AgentBridgeSignIn{++signInSaid}");

    // Each announcement its own activity, all queued: two machines' changes in one update are both read.
    int signInSaid;

    static T? FindIn<T>(DependencyObject root, string? name, string? automationId = null) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Visibility == Visibility.Visible && (name is null || t.Name == name) && (automationId is null || AutomationProperties.GetAutomationId(t) == automationId)) { return t; }
            if (FindIn<T>(child, name, automationId) is { } found) { return found; }
        }
        return null;
    }
}
