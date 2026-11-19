# The desktop a self-test attempt starts on, read and changed from outside the app (#2026).
# Dot-sourced by build/app-self-test.ps1; build/test-app-self-test-desktop.ps1 replaces its boundaries.

function Use-SelfTestContrastType {
    if ('AgentBridgeSelfTestContrast' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class AgentBridgeSelfTestContrast
{
    [StructLayout(LayoutKind.Sequential)]
    struct HIGHCONTRAST { public uint cbSize; public uint dwFlags; public System.IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint winIni);
    public static uint Get()
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        if (!SystemParametersInfo(0x42, hc.cbSize, ref hc, 0)) { throw new System.ComponentModel.Win32Exception(); }
        return hc.dwFlags;
    }
    public static void Set(uint flags)
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>(), dwFlags = flags };
        if (!SystemParametersInfo(0x43, hc.cbSize, ref hc, 0x2)) { throw new System.ComponentModel.Win32Exception(); }
    }
}
'@
}
function Get-SelfTestHighContrast { Use-SelfTestContrastType; [AgentBridgeSelfTestContrast]::Get() }
function Set-SelfTestHighContrast([uint32]$Flags) { Use-SelfTestContrastType; [AgentBridgeSelfTestContrast]::Set($Flags) }
# What is left of the app: any process started from its published folder.
function Get-SelfTestAppProcess([string]$Folder) {
    @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($Folder, [StringComparison]::OrdinalIgnoreCase) })
}
function Stop-SelfTestAppProcess($Process) {
    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    [void]$Process.WaitForExit(10000)
}

function Enter-SelfTestDesktop([string]$State, [string]$Exe) { }
