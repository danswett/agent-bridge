using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgentBridge.App.Core;

// Unattended start of a VM (decision D3, #1595, #1624): Windows signs the host's dedicated account
// in by itself after a reboot, so the host and its sessions come back with nobody at the machine.
// The credential and the Winlogon values are the host's setup-unattended and disable-unattended
// (#1623); this is the app's per-machine switch over them. Off until an approved person turns it on
// (#801), and never on a laptop.
public interface IUnattendedStart
{
    // Whether auto-logon is configured now.
    bool IsOn { get; }
    void Enable(string account);
    void Disable();
}

// Why a machine is a laptop (or otherwise cannot sign in by itself), from facts about it.
// Laptops sleep, travel and leave an unlocked console in reach, so they stay start-at-sign-in only.
public static class MachineKind
{
    public static string? LaptopReason(bool hasBattery, bool helloOnlySignIn) =>
        hasBattery ? "this machine has a battery, so it is a laptop; laptops stay start-at-sign-in only"
        : helloOnlySignIn ? "Windows Hello or PIN-only sign-in is required here, which stops Windows signing in by itself"
        : null;

    [SupportedOSPlatform("windows")]
    public static string? LaptopReasonOnThisMachine() => LaptopReason(HasBattery(), HelloOnlySignIn());

    // BatteryFlag 128 is "no system battery"; 255 is unknown, which counts as a battery: refusing is the safe error.
    [SupportedOSPlatform("windows")]
    static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag != 128;

    [SupportedOSPlatform("windows")]
    static bool HelloOnlySignIn()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device");
        return key?.GetValue("DevicePasswordLessBuildVersion") is int and not 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
