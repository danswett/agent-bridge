using System.Runtime.InteropServices;

namespace AgentBridge.App.Core;

/// <summary>
/// Tells the caller when the machine resumes from sleep or hibernation, through Windows' suspend/resume notification (a callback on a system thread:
/// no window and no message pump, so it works with the UI thread blocked, which is when it is needed). The hang watchdog counts a resume as a pause.
/// </summary>
public sealed class ResumeNotifier : IDisposable
{
    delegate uint Callback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    struct Subscription
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    const uint DeviceNotifyCallback = 2;
    const uint ResumeSuspend = 0x7;     // PBT_APMRESUMESUSPEND
    const uint ResumeAutomatic = 0x12;  // PBT_APMRESUMEAUTOMATIC

    [DllImport("powrprof.dll")]
    static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription recipient, out IntPtr handle);

    [DllImport("powrprof.dll")]
    static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);

    readonly Callback callback;   // kept alive for as long as Windows may call it
    IntPtr handle;

    public ResumeNotifier(Action resumed)
    {
        callback = (_, type, _) =>
        {
            if (type is ResumeSuspend or ResumeAutomatic)
            {
                try { resumed(); }
                catch (Exception) { /* a notice must never throw into Windows' thread */ }
            }
            return 0;
        };
        if (!OperatingSystem.IsWindows()) { return; }
        var subscription = new Subscription { Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero };
        if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref subscription, out var registered) == 0) { handle = registered; }
    }

    /// <summary>Whether Windows accepted the registration (false off Windows, or when it refused).</summary>
    public bool Registered => handle != IntPtr.Zero;

    public void Dispose()
    {
        var old = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (old != IntPtr.Zero && OperatingSystem.IsWindows()) { PowerUnregisterSuspendResumeNotification(old); }
    }
}
