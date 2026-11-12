using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AgentBridge.App;

public sealed partial class FeedbackDialog : ContentDialog
{
    readonly FeedbackReviewViewModel? review;
    readonly FeedbackSendViewModel? send;
    readonly DispatcherTimer retryClock = new() { Interval = TimeSpan.FromSeconds(1) };
    bool closed;
    bool choosingScreenshot;

    public FeedbackDialog(IFeedbackBundleSource? source = null, IFeedbackSender? sender = null, string? sessionId = null, string? sessionTitle = null)
    {
        InitializeComponent();
        if (source is not null && sender is not null)
        {
            var ui = new QueueDispatcher(DispatcherQueue);
            send = new FeedbackSendViewModel(sender, ui);
            review = new FeedbackReviewViewModel(source, send, sessionId, sessionTitle, ui);
            DataContext = review;
            review.PropertyChanged += OnState;
            send.PropertyChanged += OnState;
            var label = review.HasSession ? review.TurnsLabel : "No session selected for session turns";
            Turns.Content = label;
            AutomationProperties.SetName(Turns, label);
            Turns.IsEnabled = review.HasSession;
        }
        Opened += (_, _) =>
        {
            if (GetTemplateChild("CloseButton") is Button close)
            {
                AutomationProperties.SetAutomationId(close, "FeedbackClose");
                AutomationProperties.SetName(close, "Close feedback");
            }
            review?.Start();
            if (review is not null) { Note.Focus(FocusState.Programmatic); retryClock.Start(); }
        };
        Closed += (_, _) =>
        {
            closed = true;
            retryClock.Stop();
            if (send is not null) { send.PropertyChanged -= OnState; }
            if (review is not null) { review.PropertyChanged -= OnState; }
            review?.Close();
            if (review is not null) { review.Note = ""; review.ScreenshotPng = null; review.IncludeSessionTurns = false; }
            DataContext = null;
            Note.Text = "";
        };
        retryClock.Tick += (_, _) => send?.RefreshRetry();
        Draw();
    }

    void OnState(object? sender, PropertyChangedEventArgs e) => Draw();

    void Draw()
    {
        Preview.Text = review?.PreviewText ?? "";
        Choices.Visibility = Included.Visibility = Ui.Show(review is not null);
        Choices.IsEnabled = send is not null && !choosingScreenshot && !send.IsSending && send.State != FeedbackSendState.Sent;
        Send.Visibility = Ui.Show(send is not null && send.State is FeedbackSendState.Gathering or FeedbackSendState.Ready or FeedbackSendState.Sending);
        Send.IsEnabled = send?.CanSend == true && !choosingScreenshot;
        Retry.Visibility = Ui.Show(send is { State: FeedbackSendState.Failed, RetryAt: not null } || send?.CanRetry == true);
        Retry.IsEnabled = send?.CanRetry == true && !choosingScreenshot;
        RetryHint.Text = send?.RetryHint ?? "";
        var text = choosingScreenshot ? "Reading screenshot..." : (send?.Message ?? FeedbackSenderFactory.UnavailableText);
        if (text.Length == 0) { text = "Ready to send feedback."; }
        if (send is { State: FeedbackSendState.Failed, FailureReason: { } reason })
        {
            text += " " + (reason switch
            {
                FeedbackFailureReason.Offline => "No connection.",
                FeedbackFailureReason.RateLimited => "Too many reports.",
                FeedbackFailureReason.Quota => "The feedback quota is full.",
                FeedbackFailureReason.Unreachable => "The service could not be reached.",
                FeedbackFailureReason.KeyUnavailable => "No feedback encryption key is available.",
                _ => "The feedback service could not accept this report.",
            });
        }
        var changed = Status.Text != text;
        Status.Text = text;
        ScreenshotStatus.Text = review?.ScreenshotPng is { Length: > 0 } ? "Screenshot included; expand What's included to review it." : "No screenshot included.";
        AutomationProperties.SetName(Status, Status.Text);
        if (changed) { FrameworkElementAutomationPeer.CreatePeerForElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); }
    }

    async void OnSend(object sender, RoutedEventArgs e) { if (send is not null && !choosingScreenshot) { await send.SendAsync(); } }
    async void OnRetry(object sender, RoutedEventArgs e) { if (send is not null && !choosingScreenshot) { await send.RetryAsync(); } }
    void OnTurns(object sender, RoutedEventArgs e) { if (review is not null) { review.IncludeSessionTurns = Turns.IsChecked == true; } }
    void OnRemoveScreenshot(object sender, RoutedEventArgs e) { if (review is not null) { review.ScreenshotPng = null; Draw(); } }

    async void OnScreenshot(object sender, RoutedEventArgs e)
    {
        if (review is null) { return; }
        choosingScreenshot = true;
        Draw();
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
            picker.FileTypeFilter.Add(".png");
            var file = await picker.PickSingleFileAsync();
            if (file is null || closed) { return; }
            using var stream = await file.OpenStreamForReadAsync();
            var bytes = new byte[FeedbackLimits.MaxScreenshotBytes + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
            if (!closed) { review.ScreenshotPng = bytes[..count]; Draw(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            if (!closed) { review.ScreenshotPng = []; Draw(); }
            AppLog.Write("feedback: the chosen screenshot could not be read");
        }
        finally { choosingScreenshot = false; if (!closed) { Draw(); } }
    }
}
