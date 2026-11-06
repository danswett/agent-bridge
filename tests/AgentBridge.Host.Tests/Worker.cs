// A row of a console as the injector worker reads it (AgentBridge.Injector, Window): only the
// window's padding, ASCII spaces, trimmed from its end. The fake consoles read rows so and hold
// a send's Expect to them so, as the worker does. A fake that trimmed every white space on both
// sides hid a host that held Claude's prompt as "❯" while the worker read "❯\u00A0" off the
// screen, and refused every Claude reply (#843 review).
static class Worker
{
    public static string Read(string row) => row.TrimEnd(' ');
}
