using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static ResizeDiagnostics? resizeDiagnostics;

    sealed class ResizeDiagnostics : IDisposable
    {
        readonly MainWindow window;
        readonly string outFile;
        readonly Action<bool, string> check;
        readonly List<object> states = [];
        long layoutUpdates;
        string stage = "self-test start";
        object? lastState;
        public int Unhandled { get; private set; }
        public bool ResizeStepQueued { get; set; }

        public ResizeDiagnostics(MainWindow window, string outFile, Dictionary<string, object?> result, Action<bool, string> check)
        {
            this.window = window;
            this.outFile = outFile;
            this.check = check;
            result["resizeDiagnostics"] = states;
            window.RootElement.LayoutUpdated += OnLayout;
            Application.Current.UnhandledException += OnUnhandled;
            Record(stage);
        }

        void OnLayout(object? sender, object args) => layoutUpdates++;

        public void Record(string next)
        {
            stage = next;
            var root = window.RootElement;
            var size = window.AppWindow.Size;
            lastState = new
            {
                stage, requestedTheme = root.RequestedTheme.ToString(), actualTheme = root.ActualTheme.ToString(),
                highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast,
                windowWidth = size.Width, windowHeight = size.Height,
                rootWidth = root.ActualWidth, rootHeight = root.ActualHeight,
                desiredWidth = root.DesiredSize.Width, desiredHeight = root.DesiredSize.Height,
                xamlWidth = root.XamlRoot.Size.Width, xamlHeight = root.XamlRoot.Size.Height,
                scale = root.XamlRoot.RasterizationScale, layoutUpdates,
                pendingLayout = "not exposed by WinUI", resizeStepQueued = ResizeStepQueued,
                openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count,
            };
            states.Add(lastState);
            Save(".resize-state.json", new { processId = Environment.ProcessId, states });
            AppLog.Write($"self-test: resize state {JsonSerializer.Serialize(lastState)}");
        }

        void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            Unhandled++;
            // Read no XAML while it is failing: keep the last observation, not a second layout.
            var failure = new
            {
                processId = Environment.ProcessId, stage, layoutUpdates, resizeStepQueued = ResizeStepQueued,
                hresult = $"0x{args.Exception.HResult:X8}", xamlMessage = args.Message,
                exception = args.Exception.ToString(), handlerStack = Environment.StackTrace, lastState,
            };
            Save(".xaml-failure.json", failure);
            AppLog.Write($"self-test: unhandled XAML {JsonSerializer.Serialize(failure)}");
            // Never set Handled: diagnostics must not turn a crashing app into a passing test.
        }

        void Save(string extension, object value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                File.WriteAllText(Path.ChangeExtension(outFile, extension), JsonSerializer.Serialize(value));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                check(false, $"captures resize diagnostics {extension}: {e.Message}");
            }
        }

        public void Dispose()
        {
            window.RootElement.LayoutUpdated -= OnLayout;
            Application.Current.UnhandledException -= OnUnhandled;
            resizeDiagnostics = null;
        }
    }

    static async Task CheckResizeAfterHighContrastSignIn(MainWindow window, Action<bool, string> check)
    {
        var trace = resizeDiagnostics!;
        trace.Record("after high-contrast sign-in capture");
        var before = window.AppWindow.Size;
        var unhandled = trace.Unhandled;
        trace.Record("high-contrast sign-in resize probe");
        try
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(before.Width + 1, before.Height));
            await CaptureFrame(window.RootElement);
        }
        finally
        {
            window.AppWindow.Resize(before);
        }
        await CaptureFrame(window.RootElement);
        trace.Record("high-contrast sign-in resize restored");
        check(trace.Unhandled == unhandled, "resizes immediately after the high-contrast sign-in capture without an unhandled XAML exception");
    }
}
