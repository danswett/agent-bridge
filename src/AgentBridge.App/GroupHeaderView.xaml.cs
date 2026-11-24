using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// One group header (GroupHeaderView.xaml). The header is a record that is replaced when anything about it changes, so the bindings
// are updated when it is set; pressing it tells the view model, which draws the list again.
public sealed partial class GroupHeaderView : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(GroupHeader), typeof(GroupHeaderView), new PropertyMetadata(null, (d, _) => ((GroupHeaderView)d).Bindings.Update()));

    public GroupHeaderView() => InitializeComponent();

    public GroupHeader? Header
    {
        get => GetValue(HeaderProperty) as GroupHeader;
        set => SetValue(HeaderProperty, value);
    }

    void OnToggle(object sender, RoutedEventArgs e) => Header?.Toggle();
}
