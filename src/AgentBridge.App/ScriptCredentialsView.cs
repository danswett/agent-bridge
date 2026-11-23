using AgentBridge.App.Core;
using AgentBridge.Local;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// Settings > Script credentials (HA retirement row 1). Names, scope, expiry and last use per script; Add, Rotate, Revoke and the launch tick,
// each behind a confirmation dialog, then the host's own confirmation (ScriptCredentialsModel). It never shows or copies a secret.
public sealed class ScriptCredentialsView : UserControl
{
    readonly StackPanel rows = new() { Spacing = 8 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly TextBox name = new() { PlaceholderText = "Script name, for example pump-wake", MaxLength = 64 };
    readonly Button add = new() { Content = "Add script credential" };
    readonly List<Button> actionButtons = [];
    SettingsViewModel? viewModel;
    string? drawnKey;
    bool working;

    public ScriptCredentialsView()
    {
        AutomationProperties.SetName(name, "New script credential name");
        AutomationProperties.SetName(add, "Add script credential");
        var panel = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Script credentials", Style = (Style)Application.Current.Resources["AbTypeRow"] };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        foreach (var element in new UIElement[] { heading, name, add, status, rows }) { panel.Children.Add(element); }
        Content = panel;
        // A keystroke only decides whether Add is enabled; redrawing the rows here would drop keyboard focus from a row button.
        name.TextChanged += (_, _) => add.IsEnabled = Idle && ScriptCredentialsModel.ValidName(name.Text.Trim());
        add.Click += async (_, _) => await PressAsync(HostOps.ScriptAdd, name.Text.Trim(), then: () => name.Text = "");
    }

    public void Attach(SettingsViewModel? model)
    {
        if (viewModel is { } old) { old.PropertyChanged -= Changed; }
        viewModel = model;
        if (viewModel is { } vm) { vm.PropertyChanged += Changed; }
        Draw();
    }

    public async void Opened() { if (viewModel is { } vm) { await vm.ScriptCredentials.RefreshAsync(); Draw(); } }

    void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Draw);

    bool Idle => !working && viewModel?.Busy == false;

    void Draw()
    {
        var model = viewModel?.ScriptCredentials;
        Visibility = model is { Available: true } && viewModel!.HostRunning ? Visibility.Visible : Visibility.Collapsed;
        if (model is null) { return; }
        var idle = Idle;
        add.IsEnabled = idle && ScriptCredentialsModel.ValidName(name.Text.Trim());
        status.Text = model.Notice ?? (model.Rows.Count == 0 ? "No script credentials on this PC." : "");
        // The rows are rebuilt only when the list changed; otherwise the buttons keep their place (and keyboard focus) and only their
        // enabled state follows the page being busy.
        if (model.RowsKey == drawnKey)
        {
            foreach (var button in actionButtons) { button.IsEnabled = idle; }
            return;
        }
        drawnKey = model.RowsKey;
        rows.Children.Clear();
        actionButtons.Clear();
        foreach (var row in model.Rows)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Text = $"{row.Name}: {row.Scope}, {row.Live} live, expires {row.ExpiresAt.LocalDateTime:g}, "
                    + (row.LastUse is { } used ? $"last used {used.LocalDateTime:g} (since this host started)" : "not used since this host started")
                    + (row.FleetLaunch ? ", may launch worker sessions" : "") };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Button($"Rotate {row.Name}", "Rotate", idle, () => PressAsync(HostOps.ScriptAdd, row.Name)));
            buttons.Children.Add(Button($"Revoke {row.Name}", "Revoke", idle, () => PressAsync(HostOps.ScriptRevoke, row.Name)));
            buttons.Children.Add(Button($"{(row.FleetLaunch ? "Stop" : "Allow")} launching for {row.Name}", row.FleetLaunch ? "Stop launching" : "Allow launching", idle,
                () => PressAsync(row.FleetLaunch ? HostOps.ScriptRevokeFleet : HostOps.ScriptGrantFleet, row.Name)));
            rows.Children.Add(new StackPanel { Spacing = 4, Children = { text, buttons } });
        }
    }

    Button Button(string automationName, string label, bool enabled, Func<Task> press)
    {
        var button = new Button { Content = label, IsEnabled = enabled };
        AutomationProperties.SetName(button, automationName);
        button.Click += async (_, _) => await press();
        actionButtons.Add(button);
        return button;
    }

    async Task PressAsync(string op, string script, Action? then = null)
    {
        if (working || viewModel is not { } vm || XamlRoot is null || !ScriptCredentialsModel.ValidName(script)) { return; }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Confirm", PrimaryButtonText = "Confirm", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = vm.ScriptCredentials.ConsentFor(op, script), TextWrapping = TextWrapping.Wrap },
        };
        AutomationProperties.SetName(dialog, "Person confirmation: script credential");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { return; }
        working = true; Draw();
        try
        {
            var model = vm.ScriptCredentials;
            if (op == HostOps.ScriptAdd) { await model.AddAsync(script); }
            else if (op == HostOps.ScriptRevoke) { await model.RevokeAsync(script); }
            else { await model.SetLaunchAsync(script, op == HostOps.ScriptGrantFleet); }
            if (model.Notice is null) { then?.Invoke(); }
        }
        finally { working = false; Draw(); }
    }
}
