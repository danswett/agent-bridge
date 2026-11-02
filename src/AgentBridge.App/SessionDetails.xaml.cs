using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed partial class SessionDetails : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(SessionDetails), new PropertyMetadata(null, (d, _) => ((SessionDetails)d).Changed()));

    public SessionDetails()
    {
        InitializeComponent();
        Loaded += (_, _) => Bindings.Update();
        // Let go of the card off screen, so a closed flyout or a hidden inspector keeps nothing alive.
        Unloaded += (_, _) => { if (!IsLoaded) { Bindings.StopTracking(); } };
    }

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    public static Style ValueStyle(bool mono) => (Style)Application.Current.Resources[mono ? "AbTypeCode" : "AbTypeRow"];
    public static string ValueId(string label) => $"Detail:{label}";

    void Changed()
    {
        if (IsLoaded) { Bindings.Update(); }
    }

    void OnEnd(object sender, RoutedEventArgs e) => Card?.End?.Press();
    void OnResendEnd(object sender, RoutedEventArgs e) => Card?.End?.Resend();
}