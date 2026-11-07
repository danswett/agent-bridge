using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// A compact disclosure (ui.md §5.3, Disclosure), as the PWA's: a leading chevron and its words at
// caption size, as wide as they are, heard as expanded or collapsed (the PWA's aria-expanded).
// WinUI's Expander draws a full-width header with its chevron at the trailing edge, pointing up
// while shut, and larger words than the label above it; #504's design review asked for this.
// Enter and Space press it as any button; what it opens is the caller's, shown from IsExpanded.
public sealed partial class DisclosureButton : Button
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(DisclosureButton),
        new PropertyMetadata(false, (d, e) => ((DisclosureButton)d).Announce((bool)e.OldValue, (bool)e.NewValue)));

    public DisclosureButton() => Click += (_, _) => IsExpanded = !IsExpanded;

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    static ExpandCollapseState State(bool open) => open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    void Announce(bool was, bool now)
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, State(was), State(now));
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    // A button that also expands and collapses, as the PWA's button with aria-expanded is heard.
    sealed partial class Peer(DisclosureButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        protected override string GetClassNameCore() => nameof(DisclosureButton);
        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

        public ExpandCollapseState ExpandCollapseState => State(owner.IsExpanded);
        public void Expand() => owner.IsExpanded = true;
        public void Collapse() => owner.IsExpanded = false;
    }
}
