using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    void HookConversationPointer(ScrollViewer scroll)
    {
        scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => ReleasePin()), true);
    }

    // The person is scrolling: the view is theirs from here, and stays where they leave it.
    internal void ReleasePin() { pinEnd = false; anchoring = null; pinReleases++; }

    // Older turns can finish measuring after KeepPlace restored an earlier end. Follow the
    // measured end on layout and final view changes, including while an older target is pending.
    void FollowPinnedEnd()
    {
        if (panning || keyTarget is not null || !ConversationList.IsLoaded || ConversationScroll is not { } scroll) { return; }
        if (ConversationEndPin.Correction(pinEnd, scroll.VerticalOffset, scroll.ScrollableHeight, anchoring) is { } to) { Anchoring(scroll, to); }
    }

    void Anchoring(ScrollViewer scroll, double to)
    {
        anchoring = to;
        anchorAt = to;
        scroll.ChangeView(null, to, null, disableAnimation: true);
    }
}

static class ConversationEndPin
{
    public static double? Correction(bool pinned, double offset, double extent, double? pending) =>
        pinned && Math.Abs(extent - offset) > 0.5
        && (pending is not { } asked || Math.Abs(extent - asked) > 0.5) ? extent : null;
}
