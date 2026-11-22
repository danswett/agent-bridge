using System.ComponentModel;
using System.Text.Json;
using AgentBridge.App.Core.Grants;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed partial class GrantIssueView : UserControl
{
    GrantIssueViewModel? viewModel;
    bool busy;
    string? reviewedDocument;
    string? shownDigest, shownState;

    public GrantIssueView() => InitializeComponent();

    public GrantIssueViewModel ViewModel
    {
        get => viewModel!;
        set
        {
            if (viewModel is not null) { viewModel.PropertyChanged -= OnChanged; }
            viewModel = value;
            viewModel.PropertyChanged += OnChanged;
            reviewedDocument = null;
            shownDigest = viewModel.Digest;
            shownState = viewModel.State;
            PersonDigest.Text = "";
            Bindings.Update();
        }
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (viewModel is null) { return; }
        if (shownDigest != viewModel.Digest)
        { shownDigest = viewModel.Digest; PersonDigest.Text = ""; }
        if (shownState != viewModel.State)
        {
            shownState = viewModel.State;
            DispatcherQueue.TryEnqueue(() =>
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(StatusLine)?
                    .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged));
        }
    }

    async void OnPrepare(object sender, RoutedEventArgs e)
    {
        if (busy || viewModel is null) { return; }
        InputProblem.Text = "";
        reviewedDocument = null;
        PersonDigest.Text = "";
        JsonDocument document;
        try { document = JsonDocument.Parse(SourceDocument.Text, new() { MaxDepth = 12 }); }
        catch (JsonException) { InputProblem.Text = "Enter a valid explicit grant document before reviewing."; return; }
        using (document)
        {
            busy = true;
            var source = SourceDocument.Text;
            try
            {
                await viewModel.PrepareAsync(document.RootElement);
                if (viewModel.CanConfirm) { reviewedDocument = source; }
            }
            finally { busy = false; }
        }
    }

    async void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (busy || viewModel is not { CanConfirm: true } model) { return; }
        if (reviewedDocument != SourceDocument.Text)
        { InputProblem.Text = "The document changed. Review it again before confirming."; return; }
        busy = true;
        try { await model.ConfirmAsync(PersonDigest.Text); }
        finally { busy = false; PersonDigest.Text = ""; }
    }
}
