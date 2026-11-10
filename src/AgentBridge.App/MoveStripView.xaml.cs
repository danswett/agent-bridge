using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed partial class MoveStripView : UserControl
{
    public static readonly DependencyProperty MoveProperty = DependencyProperty.Register(nameof(Move), typeof(MoveProgressViewModel), typeof(MoveStripView), new PropertyMetadata(null, (d, e) => ((MoveStripView)d).Bind(e.OldValue as MoveProgressViewModel, e.NewValue as MoveProgressViewModel)));

    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTimeOffset stepSince;

    public MoveStripView()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        clock.Tick += (_, _) => Move?.Tick(DateTimeOffset.UtcNow - stepSince);
        Unloaded += (_, _) => clock.Stop();
    }

    public MoveProgressViewModel? Move { get => (MoveProgressViewModel?)GetValue(MoveProperty); set => SetValue(MoveProperty, value); }

    void Bind(MoveProgressViewModel? old, MoveProgressViewModel? now)
    {
        if (old is not null) { old.PropertyChanged -= OnChanged; old.Announced -= Say; }
        clock.Stop();
        Visibility = now is null ? Visibility.Collapsed : Visibility.Visible;
        StepList.ItemsSource = now?.Steps;
        if (now is null) { return; }
        now.PropertyChanged += OnChanged;
        now.Announced += Say;
        stepSince = DateTimeOffset.UtcNow;
        OnChanged(null, null!);
    }

    void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Move is not { } m) { return; }
        HeadText.Text = m.Head;
        AutomationProperties.SetName(Region, m.Head);
        GoTo.Visibility = Ui.Show(m.CanGoToIt);
        Retry.Visibility = Dismiss.Visibility = Ui.Show(m.AcceptsAnswers);
        if (m.IsMoving) { clock.Start(); } else { clock.Stop(); }
    }

    void Say(string text, bool assertive)
    {
        stepSince = DateTimeOffset.UtcNow;
        FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            assertive ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            text, "AgentBridgeMove");
    }

    void OnGoTo(object sender, RoutedEventArgs e) => Move?.GoToIt();
    void OnRetry(object sender, RoutedEventArgs e) => Move?.TryAgain();
    void OnClose(object sender, RoutedEventArgs e) => Move?.Close();
}
