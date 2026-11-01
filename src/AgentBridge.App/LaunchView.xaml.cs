using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

public sealed partial class LaunchView : UserControl
{
    LaunchViewModel? viewModel;

    public LaunchView() => InitializeComponent();

    public LaunchViewModel ViewModel
    {
        get => viewModel!;
        set
        {
            if (viewModel is not null) { viewModel.PropertyChanged -= OnChanged; }
            viewModel = value;
            viewModel.PropertyChanged += OnChanged;
            WatchCards();
            Bindings.Update();
        }
    }

    // A launch's progress and outcome come long after the press: a screen reader hears each one.
    // The peer is made if it is not there yet: the line starts collapsed, and a screen reader may
    // never have visited it, so FromElement would find none and the first outcome would go unheard.
    void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaunchViewModel.TrustQuestions)) { WatchCards(); return; }
        if (e.PropertyName != nameof(LaunchViewModel.StatusText) || string.IsNullOrEmpty(viewModel?.StatusText)) { return; }
        Announce(StatusLine);
    }

    // Each folder-trust card's progress and outcome is heard too, from its own status line.
    readonly HashSet<TrustCard> watchedCards = [];

    void WatchCards()
    {
        var now = viewModel?.TrustQuestions ?? [];
        foreach (var gone in watchedCards.Where(c => !now.Contains(c)).ToList()) { gone.PropertyChanged -= OnCardChanged; watchedCards.Remove(gone); }
        foreach (var card in now.Where(watchedCards.Add)) { card.PropertyChanged += OnCardChanged; }
    }

    void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrustCard.StatusText) || sender is not TrustCard { ShowStatus: true } card) { return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (TrustList.ContainerFromItem(card) is DependencyObject container && Find(container, "TrustStatus") is { } line) { Announce(line); }
        });
    }

    static FrameworkElement? Find(DependencyObject root, string automationId)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement f && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(f) == automationId) { return f; }
            if (Find(child, automationId) is { } found) { return found; }
        }
        return null;
    }

    void Announce(FrameworkElement line) =>
        DispatcherQueue.TryEnqueue(() => Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(line)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged));

    public static Brush ProblemBrush() => Ui.ToneBrush(Tone.Bad);
    public static Brush NoteBrush(bool warns) => warns ? Ui.Brush("AbWarningText") : Ui.Brush("AbMuted");
    public static bool Has(string? text) => !string.IsNullOrEmpty(text);

    // The pickers' choices go to the view model by hand: a two-way x:Bind on SelectedItem cannot
    // cast object to Choice, and a picker whose items were just replaced says null, which the
    // view model ignores.
    void OnPicked(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is null || sender is not ComboBox { SelectedItem: Choice choice, Tag: string field }) { return; }
        switch (field)
        {
            case "machine": viewModel.SelectedMachine = choice; break;
            case "resume": viewModel.Resume = choice; break;
            case "agent": viewModel.Agent = choice; break;
            case "workspace": viewModel.Workspace = choice; break;
            case "profile": viewModel.Profile = choice; break;
            case "model": viewModel.Model = choice; break;
            case "effort": viewModel.Effort = choice; break;
            case "context": viewModel.Context = choice; break;
            case "permissions": viewModel.Permissions = choice; break;
        }
    }

    // A press the form refuses is answered aloud every time, even with the same reason as the last:
    // otherwise a screen reader hears nothing happen.
    void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (viewModel is null) { return; }
        viewModel.Launch();
        if (!string.IsNullOrEmpty(viewModel.Problem)) { Announce(ProblemLine); }
    }
    void OnResend(object sender, RoutedEventArgs e) => viewModel?.Resend();
    void OnOpen(object sender, RoutedEventArgs e) => viewModel?.OpenSession();
    void OnReload(object sender, RoutedEventArgs e) => _ = viewModel?.RefreshAsync();

    static TrustCard? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as TrustCard;
    void OnTrust(object sender, RoutedEventArgs e) => CardOf(sender)?.Trust();
    void OnQuit(object sender, RoutedEventArgs e) => CardOf(sender)?.Quit();
    void OnTrustResend(object sender, RoutedEventArgs e) => CardOf(sender)?.Resend();
    async void OnTrustAllow(object sender, RoutedEventArgs e) { if (CardOf(sender) is { } c) { await c.AllowHereAsync(); } }
}
