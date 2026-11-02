using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed partial class ToolList : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(ToolList), new PropertyMetadata(null, (d, _) => ((ToolList)d).Changed()));

    public ToolList()
    {
        InitializeComponent();
        Loaded += (_, _) => Bindings.Update();
        Unloaded += (_, _) => { if (!IsLoaded) { Bindings.StopTracking(); } };
    }

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    void Changed()
    {
        if (IsLoaded) { Bindings.Update(); }
    }
}