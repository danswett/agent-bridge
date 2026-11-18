using System.ComponentModel;
using System.Reflection;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

public sealed class ProcessControlTests
{
    [WindowsFact]
    public void A_failed_termination_reports_fixed_words_and_the_numeric_code_not_the_OS_message()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // A null borrowed handle forces ERROR_INVALID_HANDLE without opening or ending a process.
        using var handle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var type = typeof(WindowsProcessControl).GetNestedType("Held", BindingFlags.NonPublic);
        Assert.NotNull(type);
        using var held = Assert.IsAssignableFrom<IHeldProcess>(Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [handle, 0], culture: null));

        var reason = held.Terminate();

        Assert.NotNull(reason);
        Assert.Equal("the host could not terminate the session's process (error 6)", reason);
        Assert.DoesNotContain(new Win32Exception(6).Message, reason, StringComparison.Ordinal);
    }
}
