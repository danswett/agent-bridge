using AgentBridge.App.Core;

namespace AgentBridge.App;

static partial class SelfTest
{
    static void CheckLatestNativeReceipt(SessionView view, LatestInputReceipt? receipt, Action<bool, string> check)
    {
        check(receipt is { } arrived && view.AcceptsLatestNativeReceipt(arrived),
            $"latest: native input receipt belongs to this exact view and its latest issued input ({receipt?.Offset:0.##}/{receipt?.Extent:0.##})");
    }
}
