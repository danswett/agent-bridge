namespace AgentBridge.App.Core;

public static class MessageRender
{
    public static bool TryDraw(Action draw, Action<Exception> failed)
    {
        try { draw(); return true; }
        catch (Exception e) { failed(e); return false; }
    }

    // Exceptions can quote message text; log their type and HRESULT, never their message.
    public static string Failure(string session, int item, int bytes, Exception error) =>
        $"render-failure session={session} item={item} bytes={bytes} exception={error.GetType().FullName} hresult=0x{error.HResult:X8}";
}
