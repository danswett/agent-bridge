using AgentBridge.Transport;

namespace AgentBridge.App.Core.Feedback;

// Why the app can or cannot send feedback right now. Only Available opens the dialog with Send.
public enum FeedbackAvailability
{
    Available,
    // The person turned feedback upload off in the app's settings.
    UploadOff,
    // No feedback public key (and key id) is configured: nothing to seal to.
    NoRecipientKey,
    // The app has no relay connection.
    NoRelay,
    // Connected, but the relay says it takes no feedback bundles.
    RelayNotAccepting,
}

// Builds the sender the Send feedback dialog uses, or says why it cannot (#738 S3c). Product's rule:
// when unavailable the dialog is read-only with Send disabled and Text is shown; there is no
// plaintext fallback and no email. Nothing is cached: the relay connection comes and goes, so
// each call looks at the app's settings and relay as they are now.
public sealed class FeedbackSenderFactory(Func<AppPreferences> preferences, Func<RelayClient?> relay, HttpClient http, TimeProvider? clock = null)
{
    // The only words the dialog shows when Availability is not Available.
    public const string UnavailableText = "Sending feedback isn't available right now";

    // The production recipient. Empty until Trust and the Feedback Inbox owner hand over the key
    // id and public key (base64url, 32 bytes); empty keeps feedback unavailable. Fill both together.
    public const string ProductionKeyId = "";
    public const string ProductionPublicKey = "";

    public FeedbackAvailability Availability => Evaluate().Availability;

    public bool IsAvailable => Availability == FeedbackAvailability.Available;

    // The sender for the relay as it is now, or null when Availability is not Available.
    public IFeedbackSender? TryCreate() => Evaluate().Sender;

    // The settings are a staging override: used only when both are set, otherwise the compiled-in key.
    static bool Overridden(AppPreferences p) => !string.IsNullOrWhiteSpace(p.FeedbackKeyId) && !string.IsNullOrWhiteSpace(p.FeedbackPublicKey);
    static string? KeyId(AppPreferences p) => Overridden(p) ? p.FeedbackKeyId : ProductionKeyId;
    static string? PublicKey(AppPreferences p) => Overridden(p) ? p.FeedbackPublicKey : ProductionPublicKey;

    (FeedbackAvailability Availability, IFeedbackSender? Sender) Evaluate()
    {
        var prefs = preferences();
        if (!prefs.FeedbackUpload) { return (FeedbackAvailability.UploadOff, null); }
        if (FeedbackRecipient.TryParse(KeyId(prefs), PublicKey(prefs)) is not { } recipient) { return (FeedbackAvailability.NoRecipientKey, null); }
        if (relay() is not { } client) { return (FeedbackAvailability.NoRelay, null); }
        if (client.FeedbackMaxBytes <= 0) { return (FeedbackAvailability.RelayNotAccepting, null); }
        return (FeedbackAvailability.Available, new FeedbackSender(new RelayFeedbackUplink(client, http), recipient, clock));
    }
}
