using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// A tap question's option (ui.md §6.5, "Tap to answer"). The option that sent the answer stays
// enabled at full opacity while the others are disabled, and pressing it again does nothing
// (QuestionView). Its ring, check and ✓ show that, but a screen reader met only an enabled button
// whose Invoke did nothing, with no word that it was the answer sent (#625, Copilot 4485379095).
// So its peer has the Toggle pattern as well as Invoke, on while it is the answer that went.
// Drawn as any button: it keeps Button's style.
public sealed partial class OptionButton : Button
{
    bool pressed;

    public bool Pressed
    {
        get => pressed;
        set
        {
            if (pressed == value) { return; }
            pressed = value;
            FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, State(!value), State(value));
        }
    }

    static ToggleState State(bool on) => on ? ToggleState.On : ToggleState.Off;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed partial class Peer(OptionButton owner) : ButtonAutomationPeer(owner), IToggleProvider
    {
        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPatternCore(patternInterface);

        public ToggleState ToggleState => State(owner.pressed);

        // Toggled is pressed: it answers only while the question can be answered, as a click does.
        public void Toggle() => Invoke();
    }
}
