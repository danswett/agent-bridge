using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace AgentBridge.App;

// Text onto the Windows clipboard, for every Copy in the app (#1090): says whether it got there.
static class ClipboardText
{
    // false if the clipboard refused the text (another program can hold it open), and then only.
    // SetContent is the copy. Flush only keeps the text after the app exits, and Windows refuses it
    // (COM or access denied) unless the window is in front: that refusal leaves the text on the
    // clipboard while the app runs, so it is no failed copy. An access-denied Flush used to
    // escape and show "Copy failed" over a clipboard that held the text.
    public static bool Put(string text)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException) { return false; }
        try { Clipboard.Flush(); } catch (Exception e) when (e is COMException or UnauthorizedAccessException) { }
        return true;
    }
}
