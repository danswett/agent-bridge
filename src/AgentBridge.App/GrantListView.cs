using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed class GrantListView : UserControl
{
    readonly StackPanel rows = new() { Spacing = 12 };
    readonly List<(StackPanel Panel, ContentControl Head, TextBlock[] Text, Button Revoke)> cards = [];
    readonly TextBlock status = Words("", "AbTypeMeta"), empty = Words("No grants in this receiver snapshot.", "AbTypeRow");
    readonly ContentControl heading = Heading(Words("Grants", "AbTypeTitle"), AutomationHeadingLevel.Level1);
    readonly Button refresh = new() { Content = "Refresh" }, newGrant = new() { Content = "New grant", IsEnabled = false }, back = new() { Content = "Back to Fleet", IsEnabled = false };
    GrantListViewModel? viewModel;
    Action? newGrantRequested, backRequested;
    Button? parked;
    bool working;
    public event Action? NewGrantRequested {
        add { newGrantRequested += value; DispatcherQueue.TryEnqueue(Draw); } remove { newGrantRequested -= value; DispatcherQueue.TryEnqueue(Draw); }
    }
    public event Action? BackRequested {
        add { backRequested += value; DispatcherQueue.TryEnqueue(Draw); } remove { backRequested -= value; DispatcherQueue.TryEnqueue(Draw); }
    }
    public GrantListView() {
        var panel = new StackPanel { MaxWidth = 920, Padding = new Thickness(24, 20, 24, 24), Spacing = 12 };
        AutomationProperties.SetName(refresh, "Refresh grants"); AutomationProperties.SetName(newGrant, "New grant"); AutomationProperties.SetName(back, "Back to Fleet");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        foreach (var element in new UIElement[] { back, heading, newGrant, refresh, status, empty, rows }) { panel.Children.Add(element); }
        panel.Children.Add(Words("Revoke blocks new reservations and queued operations. Work already in flight may finish; previously launched sessions are not stopped.", "AbTypeMeta"));
        Content = new ScrollViewer { Content = new Grid { Children = { panel } }, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        refresh.Click += async (_, _) => await RefreshAsync();
        newGrant.Click += (_, _) => { if (!working && viewModel is { Available: true, Busy: false }) { newGrantRequested?.Invoke(); } };
        back.Click += (_, _) => backRequested?.Invoke();
        Loaded += (_, _) => { if (viewModel is { } vm) { vm.PropertyChanged -= Changed; vm.PropertyChanged += Changed; } Draw(); };
        Unloaded += (_, _) => { if (viewModel is { } vm) { vm.PropertyChanged -= Changed; } };
        Draw();
    }
    public GrantListViewModel? ViewModel {
        get => viewModel;
        set { if (viewModel is { } old) { old.PropertyChanged -= Changed; } viewModel = value; if (IsLoaded && viewModel is { } vm) { vm.PropertyChanged += Changed; } DispatcherQueue.TryEnqueue(Draw); }
    }
    void Changed(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Draw);
    public async void Opened() { back.Focus(FocusState.Programmatic); await RefreshAsync(); }
    async Task RefreshAsync() { if (viewModel is { } vm) { await RunAsync(vm.RefreshAsync); } }
    async Task RunAsync(Func<Task> action) {
        if (working || viewModel?.Busy == true) { return; } working = true; Draw();
        try { await action(); } finally { working = false; Draw(); }
    }
    void Draw() {
        var vm = viewModel; var busy = working || vm?.Busy == true;
        SetEnabled(refresh, vm is not null && !busy, heading); SetEnabled(newGrant, newGrantRequested is not null && vm?.Available == true && !busy, heading); SetEnabled(back, backRequested is not null, heading);
        status.Text = string.Join("\n", new[] { vm?.Error ?? "Grants unavailable: no receiver connected.", busy ? "Waiting for the receiver..." : "" }.Where(s => !string.IsNullOrEmpty(s)));
        AutomationProperties.SetName(status, status.Text); status.Visibility = Ui.Some(status.Text); empty.Visibility = Ui.Show(vm is not null && !busy && vm.Rows.Count == 0 && status.Text.Length == 0);
        while (cards.Count > (vm?.Rows.Count ?? 0)) {
            var last = cards[^1]; if (last.Head.FocusState != FocusState.Unfocused || last.Revoke.FocusState != FocusState.Unfocused) { parked = null; FocusHeading(heading, FocusState.Programmatic); }
            rows.Children.Remove(last.Panel); cards.RemoveAt(cards.Count - 1);
        }
        if (vm is null) { return; }
        for (var i = 0; i < vm.Rows.Count; i++) {
            if (i == cards.Count) { AddCard(); }
            var row = vm.Rows[i]; var card = cards[i];
            var lines = new[] { row.Title, row.Details, $"State: {row.State}", $"Counters: {row.Counters}", $"In flight: {row.InFlight}", $"Receipt: {row.Receipt}" };
            for (var j = 0; j < lines.Length; j++) { card.Text[j].Text = lines[j]; }
            AutomationProperties.SetName(card.Head, row.Title); AutomationProperties.SetName(card.Revoke, $"Revoke {row.Title}");
            card.Revoke.Tag = (vm, row); SetEnabled(card.Revoke, row.CanRevoke && !busy, card.Head);
        }
    }
    void AddCard() {
        var panel = new StackPanel { Spacing = 6 }; var text = Enumerable.Range(0, 6).Select(i => Words("", i < 2 ? "AbTypeRow" : "AbTypeMeta")).ToArray();
        var head = Heading(text[0], AutomationHeadingLevel.Level2); panel.Children.Add(head); foreach (var line in text.Skip(1)) { panel.Children.Add(line); }
        var revoke = new Button { Content = "Revoke" };
        revoke.Click += async (_, _) => { if (revoke.Tag is ValueTuple<GrantListViewModel, GrantListRow> target && viewModel == target.Item1 && target.Item2.CanRevoke) { await RunAsync(() => target.Item1.RevokeAsync(target.Item2, ConfirmAsync)); } };
        panel.Children.Add(revoke); rows.Children.Add(panel); cards.Add((panel, head, text, revoke));
    }
    static ContentControl Heading(TextBlock text, AutomationHeadingLevel level) {
        var head = new ContentControl { Content = text, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, UseSystemFocusVisuals = true };
        AutomationProperties.SetHeadingLevel(head, level); AutomationProperties.SetName(head, text.Text);
        head.LostFocus += (_, _) => head.IsTabStop = false; return head;
    }
    static void FocusHeading(ContentControl head, FocusState state) { head.IsTabStop = true; if (!head.Focus(state)) { head.IsTabStop = false; } }
    // Keep the same row controls; a temporarily disabled action parks focus on its heading.
    void SetEnabled(Button button, bool enabled, ContentControl head) {
        if (!enabled && button.FocusState != FocusState.Unfocused) { FocusHeading(head, button.FocusState); parked = button; }
        button.IsEnabled = enabled;
        if (enabled && parked == button) { if (head.FocusState != FocusState.Unfocused) { button.Focus(head.FocusState); } parked = null; }
    }
    async Task<bool> ConfirmAsync(string summary) {
        if (XamlRoot is null) { return false; }
        var dialog = new ContentDialog {
            XamlRoot = XamlRoot, Title = "Revoke this grant?", PrimaryButtonText = "Revoke", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = Words(summary + "\n\nWork already in flight may finish. This does not stop previously launched sessions.", "AbTypeRow"), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        AutomationProperties.SetName(dialog, "Person confirmation: revoke grant"); return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    static TextBlock Words(string text, string style) {
        var block = new TextBlock { Text = text, Style = (Style)Application.Current.Resources[style], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (style == "AbTypeMeta") { block.Foreground = Ui.Brush("AbText2"); block.ActualThemeChanged += (_, _) => block.Foreground = Ui.ThemedBrush("AbText2", block.ActualTheme); } return block;
    }
}
