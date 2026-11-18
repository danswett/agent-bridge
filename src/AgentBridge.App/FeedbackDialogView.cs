using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

static class FeedbackDialogView
{
    public static async Task ShowAsync(ContentDialog dialog, AppFeedbackSources sources, FeedbackSenderFactory senders,
        RecoveryReportOffer? offer)
    {
        using var cancel = new CancellationTokenSource();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "FeedbackStatus");
        var request = offer?.Previous is { } previous
            ? await sources.RecoveryRequestAsync(previous, "", null, cancel.Token)
            : new FeedbackRequest();
        var sender = request.Recovery is null ? null : senders.TryCreate();
        var send = new FeedbackSendViewModel(sender, new QueueDispatcher(DispatcherQueue.GetForCurrentThread()), announce: (_, urgent) =>
        {
            AutomationProperties.SetLiveSetting(status, urgent ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            FrameworkElementAutomationPeer.CreatePeerForElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, allowSending: request.Recovery is not null);
        var source = new FeedbackBundleSource(sources);
        var preview = new TextBox { Header = "Report preview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240 };
        AutomationProperties.SetAutomationId(preview, "FeedbackPreview");
        var included = new StackPanel { Spacing = 4 };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = request.Recovery is null ? "Feedback uploads are currently limited to structured recovery reports."
                : "The app closed unexpectedly. Only structured recovery metadata is included, not diagnostic text, names or session identifiers. Nothing is sent unless you choose Send report.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (request.Recovery is not null)
        {
            body.Children.Add(new TextBlock { Text = "What's included" });
            body.Children.Add(included);
            body.Children.Add(preview);
        }
        body.Children.Add(status);
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 520 };
        dialog.Title = offer is null ? "Send feedback" : "Recovered from an unexpected exit";
        dialog.PrimaryButtonText = offer is null ? "Send" : "Send report";
        dialog.CloseButtonText = offer is null ? "Close" : "Not now";
        dialog.IsPrimaryButtonEnabled = false;
        var previewReady = false;
        var shown = false;
        CancellationTokenSource? gathering = null;

        void Update()
        {
            status.Text = send.State == FeedbackSendState.Ready && !send.CanSend ? FeedbackSenderFactory.UnavailableText : send.Message;
            dialog.IsPrimaryButtonEnabled = previewReady && (send.CanSend || send.CanRetry);
            dialog.PrimaryButtonText = send.CanRetry ? send.RetryLabel : offer is null ? "Send" : "Send report";
            if (send.State == FeedbackSendState.Sent) { dialog.CloseButtonText = "Close"; }
        }
        async Task Gather()
        {
            gathering?.Cancel();
            using var current = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            gathering = current;
            previewReady = false;
            preview.Text = "";
            included.Children.Clear();
            try
            {
                var bundle = await send.PrepareAsync(source, request, current.Token);
                if (bundle is null || send.IsClosed) { return; }
                foreach (var item in bundle.Items)
                {
                    included.Children.Add(new TextBlock { Text = $"{item.Label}: {item.Summary}", TextWrapping = TextWrapping.Wrap });
                }
                preview.Text = bundle.Text;
                previewReady = true;
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Write($"feedback gathering failed: {e.GetType().Name}");
                send.GatherFailed();
            }
            finally { if (ReferenceEquals(gathering, current)) { gathering = null; } }
            Update();
        }
        send.PropertyChanged += (_, _) => Update();
        dialog.Opened += (_, _) => { shown = true; _ = Gather(); };
        dialog.Closed += (_, _) => { cancel.Cancel(); send.Close(); };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            if (request.Recovery is null) { return; }
            dialog.IsPrimaryButtonEnabled = false;
            send.RefreshSender(senders.TryCreate());
            if (send.CanRetry) { await send.RetryAsync(); }
            else if (offer is not null) { await offer.AcceptAsync(send); }
            else { await send.SendAsync(); }
            Update();
        };
        var retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        retryTimer.Tick += (_, _) => { send.RefreshSender(senders.TryCreate()); send.RefreshRetry(); };
        try
        {
            if (request.Recovery is not null) { retryTimer.Start(); }
            await dialog.ShowAsync();
        }
        finally
        {
            retryTimer.Stop();
            cancel.Cancel();
            send.Close();
            if (shown) { offer?.Dismiss(); }
        }
    }
}
