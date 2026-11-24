# The desktop a self-test attempt starts on, read and changed from outside the app (#2026).
# Dot-sourced by build/app-self-test.ps1; build/test-app-self-test-desktop.ps1 replaces its boundaries.
# The app turns Windows' high contrast on for one pass and back in a finally (SelfTest.cs,
# HighContrastPass), which a native crash skips. A retry then found it on already, took the "on
# already" branch and skipped the transition, repaint and switch-off checks, so a repeatable crash in
# that transition could pass on retry (PR 3239). So the first attempt in a job records the setting,
# and each later one stops what is left of the app, puts the setting back, reads both again and
# refuses to start on any other desktop.

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

function Enter-SelfTestDesktop([string]$State, [string]$Exe) {
    $folder = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Exe)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not (Test-Path -LiteralPath $State)) {
        $flags = Get-SelfTestHighContrast
        @{ highContrast = $flags } | ConvertTo-Json | Set-Content -LiteralPath $State
        Write-Output ('desktop recorded for later attempts: high contrast flags 0x{0:X}' -f $flags)
        return
    }
    $want = [uint32](Get-Content -LiteralPath $State -Raw | ConvertFrom-Json).highContrast
    try {
        foreach ($p in Get-SelfTestAppProcess $folder) { Stop-SelfTestAppProcess $p }
        if (((Get-SelfTestHighContrast) -band 1) -ne ($want -band 1)) { Set-SelfTestHighContrast $want }
    }
    catch { Write-Output "::error::putting the desktop back failed: $($_.Exception.Message)" }
    # Read again rather than trusting the calls above. Only the on bit decides the app's branch.
    $now = Get-SelfTestHighContrast
    $left = @(Get-SelfTestAppProcess $folder).Count
    if (($now -band 1) -ne ($want -band 1) -or $left -gt 0) {
        throw ('refused: high contrast flags are 0x{0:X} (0x{1:X} before the first attempt) and {2} app process(es) are left; this attempt could skip the checks an earlier one failed' -f $now, $want, $left)
    }
    Write-Output ('desktop as the first attempt found it: high contrast flags 0x{0:X}, no app process left' -f $now)
}
