using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// The selected delegate (DelegatesViewModel.Detail), in the inspector or the Delegates tab. Which
// one, and going back, are the panes' (PaneLayoutViewModel); its words are fetched while it shows.
public sealed partial class DelegateInspector : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(DelegateInspector), new PropertyMetadata(null, (d, _) => ((DelegateInspector)d).Changed()));

    public DelegateInspector()
    {
        InitializeComponent();
        Loaded += (_, _) => Bindings.Update();
        // Let go of the card off screen, as SessionDetails does.
        Unloaded += (_, _) => { if (!IsLoaded) { Bindings.StopTracking(); } };
    }

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    public PaneLayoutViewModel? Layout { get; set; }

    // What its back button says: back to the session's details (the inspector) or to the list (the tab).
    public string BackLabel { get; set; } = "Back to delegates";

    public static string ValueId(string label) => $"Delegate:{label}";
    public static Thickness IndentOf(double indent) => new(indent, 0, 0, 0);

    void Changed()
    {
        if (IsLoaded) { Bindings.Update(); }
    }

    void OnBack(object sender, RoutedEventArgs e) => Layout?.CloseDelegate();
    void OnAskToStop(object sender, RoutedEventArgs e) => Card?.Delegates.AskMainAgentToStop();

    void OnNested(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NestedDelegate n) { Layout?.OpenDelegate(n.Item.Id); }
    }
}
