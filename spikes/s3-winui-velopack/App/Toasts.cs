using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Foundation.Collections;

namespace AgentBridgeS3;

// Toast activations through the OS API rather than Windows App SDK's AppNotificationManager,
// whose Register() throws 0x8007007E in an unpackaged self-contained app on WinAppSDK 2.5.1
// (microsoft/WindowsAppSDK#6774), so button clicks would never reach the app.
static class Toasts
{
    static readonly object Gate = new();
    static Action<string, ToastArguments, ValueSet>? sink;
    static readonly List<(string, ToastArguments, ValueSet)> Pending = [];
    static int activations;

    // Subscribing registers the COM activator. It must happen before XAML starts: on a cold
    // start Windows launches the exe and then calls the activator, and an activation that
    // arrives before the window exists is held here rather than lost. The call can equally
    // arrive after the window is up, so "cold start" is decided by the process, not timing.
    public static void Start() => ToastNotificationManagerCompat.OnActivated += e =>
    {
        var args = ToastArguments.Parse(e.Argument);
        var how = Interlocked.Increment(ref activations) == 1 && LaunchedByToast ? "cold start" : "while running";
        Action<string, ToastArguments, ValueSet>? deliver;
        lock (Gate)
        {
            deliver = sink;
            if (deliver == null) { Pending.Add((how, args, e.UserInput)); return; }
        }
        deliver(how, args, e.UserInput);
    };

    public static void Attach(Action<string, ToastArguments, ValueSet> deliver)
    {
        List<(string, ToastArguments, ValueSet)> held;
        lock (Gate) { sink = deliver; held = [.. Pending]; Pending.Clear(); }
        foreach (var (how, args, input) in held) deliver(how, args, input);
    }

    public static bool LaunchedByToast => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    // Removes the HKCU COM server and AUMID registrations, so no dead activator is left behind.
    public static void Uninstall() => ToastNotificationManagerCompat.Uninstall();
}
