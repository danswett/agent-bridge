using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AgentBridge.App.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AgentBridge.App;

// Toasts through the OS API (Windows.UI.Notifications) and the classic desktop COM activator,
// not Windows App SDK's AppNotificationManager: in an unpackaged self-contained app its
// Register() throws 0x8007007E (microsoft/WindowsAppSDK#6774), and the clicks never arrive
// (spike S3). S3 used Microsoft.Toolkit.Uwp.Notifications for the activator; this is the small
// owned replacement its results recommend, without the toolkit's System.Drawing dependency.
static partial class ToastActivator
{
    // The activator's class id: fixed, since the registration names it and survives updates.
    public static readonly Guid Clsid = new("671f58d4-187e-47c8-bd8f-ca6103448929");

    // The id Velopack gives the app's shortcuts (velopack.<pack id>); toasts shown under it carry
    // the app's name and icon, and land on the same taskbar button.
    public const string Aumid = "velopack.AgentBridgeApp";

    static readonly Lock Gate = new();
    static readonly List<(string? Arguments, Dictionary<string, string> Inputs)> Pending = [];
    static Action<string?, Dictionary<string, string>>? sink;
    static ClassFactory? factory;
    static uint cookie;

    // Must run before XAML starts: on a cold start Windows launches the exe and then calls the
    // activator, and a click that arrives before the window exists is held, not lost (S3).
    public static void Start()
    {
        factory = new ClassFactory();
        var unknown = ComInterop.Wrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
        var hr = CoRegisterClassObject(Clsid, unknown, ClsctxLocalServer, RegclsMultipleUse, out cookie);
        if (hr < 0) { AppLog.Write($"toast activator not registered with COM: 0x{hr:X8}"); }
    }

    public static void Attach(Action<string?, Dictionary<string, string>> deliver)
    {
        List<(string?, Dictionary<string, string>)> held;
        lock (Gate) { sink = deliver; held = [.. Pending]; Pending.Clear(); }
        foreach (var (args, inputs) in held) { deliver(args, inputs); }
    }

    internal static void Activated(string? arguments, Dictionary<string, string> inputs)
    {
        Action<string?, Dictionary<string, string>>? deliver;
        lock (Gate)
        {
            deliver = sink;
            if (deliver is null) { Pending.Add((arguments, inputs)); return; }
        }
        deliver(arguments, inputs);
    }

    // Per user, under HKCU: the COM server Windows starts for a click while the app is closed,
    // and the AUMID's name, icon and activator. Velopack keeps the exe's path (current\) the
    // same across updates, so this holds after one; written on each start anyway, so a moved
    // install heals itself.
    public static void Register(string exe, string icon)
    {
        using (var server = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{{{Clsid}}}\LocalServer32"))
        {
            server.SetValue(null, $"\"{exe}\" -ToastActivated");
        }
        using var id = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
        id.SetValue("DisplayName", "Agent Bridge");
        id.SetValue("IconUri", icon);
        id.SetValue("CustomActivator", $"{{{Clsid}}}");
    }

    // Uninstall: no dead activator left behind, and nothing left in Notification Center.
    public static void Unregister()
    {
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception e) when (e is COMException or ArgumentException) { }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{Clsid}}}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Aumid}", throwOnMissingSubKey: false);
    }

    public static void Stop()
    {
        if (cookie != 0) { _ = CoRevokeClassObject(cookie); cookie = 0; }
    }

    const uint ClsctxLocalServer = 4;
    const uint RegclsMultipleUse = 1;

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid clsid, nint unknown, uint context, uint flags, out uint registration);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint registration);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string id);
}

static class ComInterop
{
    public static readonly StrategyBasedComWrappers Wrappers = new();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
internal partial interface INotificationActivationCallback
{
    void Activate(string appUserModelId, string? invokedArgs, nint data, uint count);
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, in Guid iid, out nint instance);

    [PreserveSig]
    int LockServer(int lockServer);
}

[GeneratedComClass]
internal sealed partial class ActivationCallback : INotificationActivationCallback
{
    [StructLayout(LayoutKind.Sequential)]
    struct UserInput
    {
        public nint Key;
        public nint Value;
    }

    public void Activate(string appUserModelId, string? invokedArgs, nint data, uint count)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var entry = Marshal.PtrToStructure<UserInput>(data + i * Marshal.SizeOf<UserInput>());
            var key = Marshal.PtrToStringUni(entry.Key);
            if (key is not null) { inputs[key] = Marshal.PtrToStringUni(entry.Value) ?? ""; }
        }
        ToastActivator.Activated(invokedArgs, inputs);
    }
}

[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    const int ClassENoAggregation = unchecked((int)0x80040110);
    readonly ActivationCallback callback = new();

    public int CreateInstance(nint outer, in Guid iid, out nint instance)
    {
        instance = 0;
        if (outer != 0) { return ClassENoAggregation; }
        var unknown = ComInterop.Wrappers.GetOrCreateComInterfaceForObject(callback, CreateComInterfaceFlags.None);
        try { return Marshal.QueryInterface(unknown, in iid, out instance); }
        finally { Marshal.Release(unknown); }
    }

    public int LockServer(int lockServer) => 0;
}

// The app's toasts, shown and withdrawn under its AUMID. One group, tagged by session, so a
// session has one toast at a time and an answered question's toast can be taken back.
sealed class WindowsToastSink : IToastSink
{
    const string Group = "sessions";
    ToastNotifier? notifier;

    public void Show(ToastNotice notice)
    {
        try
        {
            notifier ??= ToastNotificationManager.CreateToastNotifier(ToastActivator.Aumid);
            var doc = new XmlDocument();
            doc.LoadXml(notice.Xml);
            notifier.Show(new ToastNotification(doc) { Tag = notice.Tag, Group = Group, SuppressPopup = notice.Quiet });
        }
        catch (Exception e) when (e is COMException or ArgumentException or UnauthorizedAccessException)
        {
            AppLog.Write($"toast not shown: {e.Message}");
        }
    }

    public void Withdraw(string tag)
    {
        try { ToastNotificationManager.History.Remove(tag, Group, ToastActivator.Aumid); }
        catch (Exception e) when (e is COMException or ArgumentException) { }
    }
}

// Focus Assist, presentations and full-screen apps: Windows' own "busy" says a toast should go
// quietly to Notification Center rather than pop up over what the person is doing.
static partial class FocusAssist
{
    const int AcceptsNotifications = 5;
    const int App = 7;

    public static bool Quiet()
    {
        try { return SHQueryUserNotificationState(out var state) == 0 && state is not (AcceptsNotifications or App); }
        catch (EntryPointNotFoundException) { return false; }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
