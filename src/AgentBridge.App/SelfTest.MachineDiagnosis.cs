using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentBridge.App;

// Escape closes a machine's diagnosis from its Copy and from its Close, and focus goes back to
// that row's Diagnose (#802; ui.md §6.6). The other demo machine is diagnosed with its
// fictional snapshot; Escape follows the native window's key messages through the row's
// handlers, using the conversation checks' foreground-aware input helper.
static partial class SelfTest
{
    static async Task CheckMachineDiagnosis(MainWindow window, DemoHost remote, Action<bool, string> check)
    {
        window.ShowPage("machines");
        Grid? Row() => Find<Grid>(window.RootElement)
            .FirstOrDefault(g => AutomationProperties.GetAutomationId(g) == "MachineRow" && g.ActualHeight > 0 && ById<TextBlock>(g, "MachineName")?.Text == remote.MachineName);
        bool Open() => Row() is { } g && ById<TextBox>(g, "MachineDiagnosisText") is { Visibility: Visibility.Visible, ActualHeight: > 0 };
        if (!await Until(() => Row() is { } g && ById<Button>(g, "DiagnoseMachine") is { Visibility: Visibility.Visible, IsEnabled: true }, TimeSpan.FromSeconds(10)))
        {
            check(false, "offers Diagnose on the other demo machine's row");
            return;
        }
        foreach (var from in new[] { "CopyMachineDiagnosis", "CloseMachineDiagnosis" })
        {
            // Diagnose asks no more often than DiagnosticsProtocol.MinInterval.
            await Task.Delay(DiagnosticsProtocol.MinInterval + TimeSpan.FromMilliseconds(300));
            ((IInvokeProvider)new ButtonAutomationPeer(ById<Button>(Row()!, "DiagnoseMachine")!).GetPattern(PatternInterface.Invoke)).Invoke();
            if (!await Until(Open, TimeSpan.FromSeconds(10)))
            {
                check(false, $"shows the other machine's diagnosis to close it from {from}");
                continue;
            }
            // Copy is enabled only once the report is said to be reviewed; nothing is copied.
            if (ById<CheckBox>(Row()!, "MachineDiagnosisReviewed") is { } reviewed) { reviewed.IsChecked = true; }
            var button = ById<Button>(Row()!, from);
            await Until(() => button is { IsEnabled: true }, TimeSpan.FromSeconds(2));
            // Arm64 CI can refuse foreground activation: SendInput then succeeds for another
            // window. Use the shared focused-window path and require XAML to receive Escape.
            using var trace = new EscapeTrace(window, from);
            trace.Note($"before foreground: open {Open()}");
            var foreground = TrayIcon.SetForegroundWindow(window.Handle);
            trace.Note($"SetForegroundWindow returned {foreground}");
            var focused = button?.Focus(FocusState.Keyboard) == true && await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), button), TimeSpan.FromSeconds(2));
            trace.Note($"before Escape: focused {focused}, open {Open()}");
            var pressedBy = focused ? Press(window.Handle, VirtualKey.Escape, foreground: true, extended: false) : "none";
            trace.Note($"Escape pressed by {pressedBy}");
            var closed = focused && await Until(() => trace.SawEscape && !Open(), TimeSpan.FromSeconds(5));
            trace.Note($"after Escape: closed {closed}, open {Open()}");
            var diagnose = Row() is { } row ? ById<Button>(row, "DiagnoseMachine") : null;
            var back = closed && await Until(() => diagnose is not null && ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), diagnose), TimeSpan.FromSeconds(2));
            trace.Note($"after refocus: on the row's Diagnose {back}, Diagnose found {diagnose is not null}");
            var name = FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is FrameworkElement f ? AutomationProperties.GetAutomationId(f) : null;
            check(focused && trace.SawEscape && closed && back,
                $"closes the diagnosis on Escape from {from} and puts focus back on that row's Diagnose (focused {focused}, pressed by {pressedBy}, received {trace.SawEscape}, closed {closed}, focus on {name})");
        }
    }

    // Each step of one Escape check, to app.log: time since it began, the foreground window and
    // whether it is the app's, what XAML has focused, and each key the window's root saw (its
    // handled ones too). It changes no focus, window or key; it only looks.
    sealed class EscapeTrace : IDisposable
    {
        readonly MainWindow window;
        readonly string from;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly KeyEventHandler onKey;
        readonly List<string> keys = [];

        public bool SawEscape { get; private set; }

        public EscapeTrace(MainWindow window, string from)
        {
            this.window = window;
            this.from = from;
            onKey = (_, e) =>
            {
                keys.Add($"{e.Key} at +{clock.ElapsedMilliseconds}ms from {Describe(e.OriginalSource)} handled {e.Handled}");
                SawEscape |= e.Key == VirtualKey.Escape;
            };
            window.RootElement.AddHandler(UIElement.KeyDownEvent, onKey, true);
        }

        public void Note(string step)
        {
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var pid);
            var focus = Describe(FocusManager.GetFocusedElement(window.RootElement.XamlRoot));
            AppLog.Write($"self-test: escape trace {from} +{clock.ElapsedMilliseconds}ms: {step}; foreground 0x{foreground:X} (the app's window {foreground == window.Handle}, the app's process {pid == Environment.ProcessId}), focus on {focus}, root keys [{string.Join("; ", keys)}]");
        }

        public void Dispose() => window.RootElement.RemoveHandler(UIElement.KeyDownEvent, onKey);

        static string Describe(object? element) => element switch
        {
            null => "nothing",
            FrameworkElement f => $"{f.GetType().Name} {AutomationProperties.GetAutomationId(f)}",
            _ => element.GetType().Name,
        };
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
