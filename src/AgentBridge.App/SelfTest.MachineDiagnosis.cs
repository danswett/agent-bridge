using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AgentBridge.App;

// Escape closes a machine's diagnosis from its Copy and from its Close, and focus goes back to
// that row's Diagnose (#802; ui.md §6.6). The other demo machine is diagnosed with its
// fictional snapshot; Escape is a real key press, sent to the window with SendInput, so the
// key's way through the row's handlers is the one a person's takes.
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
            TrayIcon.SetForegroundWindow(window.Handle);
            var focused = button?.Focus(FocusState.Keyboard) == true && await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), button), TimeSpan.FromSeconds(2));
            var sent = focused && PressEscape();
            var closed = sent && await Until(() => !Open(), TimeSpan.FromSeconds(5));
            var diagnose = Row() is { } row ? ById<Button>(row, "DiagnoseMachine") : null;
            var back = closed && await Until(() => diagnose is not null && ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), diagnose), TimeSpan.FromSeconds(2));
            var name = FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is FrameworkElement f ? AutomationProperties.GetAutomationId(f) : null;
            check(focused && sent && closed && back,
                $"closes the diagnosis on Escape from {from} and puts focus back on that row's Diagnose (focused {focused}, sent {sent}, closed {closed}, focus on {name})");
        }
    }

    // Escape, down and up, as the keyboard sends it.
    static unsafe bool PressEscape()
    {
        const ushort escape = 0x1B;
        const uint keyUp = 0x0002;
        var inputs = stackalloc InjectedInput[2];
        inputs[0] = new InjectedInput { Type = 1, Union = new InjectedUnion { Key = new KeyInput { Vk = escape, Scan = 0, Flags = 0, Time = 0, ExtraInfo = 0 } } };
        inputs[1] = new InjectedInput { Type = 1, Union = new InjectedUnion { Key = new KeyInput { Vk = escape, Scan = 0, Flags = keyUp, Time = 0, ExtraInfo = 0 } } };
        return SendInput(2, inputs, sizeof(InjectedInput)) == 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    // INPUT: a type and a union sized by its largest member, MOUSEINPUT (32 bytes on 64-bit Windows).
    [StructLayout(LayoutKind.Sequential)]
    struct InjectedInput
    {
        public uint Type;
        public InjectedUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct InjectedUnion
    {
        [FieldOffset(0)] public KeyInput Key;
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint count, InjectedInput* inputs, int size);
}
