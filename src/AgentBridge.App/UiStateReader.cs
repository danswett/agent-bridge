using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// Reads the names the hang watchdog logs when the UI freezes. Run by the watchdog's ping, so on the UI thread, which is the only thread that may touch the XAML.
static class UiStateReader
{
    public static string Read(XamlRoot? root)
    {
        if (root is null) { return UiStateSnapshot.Describe([], null); }
        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => Info(p.Child ?? p)).ToArray();
        return UiStateSnapshot.Describe(popups, FocusManager.GetFocusedElement(root) is DependencyObject focused ? Info(focused) : null);
    }

    static UiElementInfo Info(DependencyObject element) =>
        new(element.GetType().Name, (element as FrameworkElement)?.Name, (element as UIElement)?.PointerCaptures?.Count ?? 0);
}
