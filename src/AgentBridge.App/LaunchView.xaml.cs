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
            viewModel = value;
            Bindings.Update();
        }
    }

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

    void OnLaunch(object sender, RoutedEventArgs e) => viewModel?.Launch();
    void OnResend(object sender, RoutedEventArgs e) => viewModel?.Resend();
    void OnOpen(object sender, RoutedEventArgs e) => viewModel?.OpenSession();
    void OnReload(object sender, RoutedEventArgs e) => _ = viewModel?.RefreshAsync();
}
