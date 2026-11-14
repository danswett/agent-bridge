function Get-Arm64Foreground {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Arm64Foreground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint fl, UIntPtr ex);
    public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Chord(byte a, byte b) { keybd_event(a, 0, 0, UIntPtr.Zero); keybd_event(b, 0, 0, UIntPtr.Zero); keybd_event(b, 0, 2, UIntPtr.Zero); keybd_event(a, 0, 2, UIntPtr.Zero); }
}
'@
    $handle = [Arm64Foreground]::GetForegroundWindow()
    $class = [Text.StringBuilder]::new(256); $title = [Text.StringBuilder]::new(512); $windowProcessId = [uint32]0
    [void][Arm64Foreground]::GetClassName($handle, $class, $class.Capacity)
    [void][Arm64Foreground]::GetWindowText($handle, $title, $title.Capacity)
    [void][Arm64Foreground]::GetWindowThreadProcessId($handle, [ref]$windowProcessId)
    $processName = if ($windowProcessId) { (Get-Process -Id $windowProcessId -ErrorAction Stop).ProcessName } else { 'none' }
    [pscustomobject]@{ Handle = $handle; Class = $class.ToString(); Title = $title.ToString(); Process = $processName }
}

function Close-Arm64Popup($handle) {
    Write-Host "WM_CLOSE posted=$([Arm64Foreground]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero))"
    Start-Sleep -Seconds 2
    [Arm64Foreground]::Chord(0x5B, 0x44) # Win+D, as proved by run 43048795338.
    Start-Sleep -Seconds 2
    [Arm64Foreground]::Key(0x1B) # Escape
    Start-Sleep -Seconds 1
}

function Test-Arm64Foreground {
    Add-Type -AssemblyName System.Windows.Forms
    $form = [System.Windows.Forms.Form]::new()
    try {
        $form.Text = 'Arm64 foreground probe'; $form.Show(); Start-Sleep -Milliseconds 800
        $ok = [Arm64Foreground]::SetForegroundWindow($form.Handle)
        Start-Sleep -Milliseconds 300
        $actual = Get-Arm64Foreground
        Write-Host "SetForegroundWindow=$ok foreground now: $actual (probe HWND $($form.Handle))"
        return ($ok -and $actual.Handle -eq $form.Handle)
    } finally { $form.Dispose() }
}

function Test-Arm64AccountPopup($owner) {
    return ($owner.Process -eq 'WWAHost' -and $owner.Class -eq 'Windows.UI.Core.CoreWindow' -and $owner.Title -eq 'Microsoft account')
}

function Invoke-Arm64PopupDismiss {
    $ErrorActionPreference = 'Stop'
    $before = Get-Arm64Foreground
    Write-Host "foreground before: $before"
    $popup = Test-Arm64AccountPopup $before
    if ($popup) { Close-Arm64Popup $before.Handle } else { Write-Host 'No Microsoft account popup; no dismissal input sent.' }
    $after = Get-Arm64Foreground
    Write-Host "foreground after: $after"
    if ($popup -and $after.Handle -eq $before.Handle -and (Test-Arm64AccountPopup $after)) { throw "Microsoft account popup still owns foreground after dismissal. foreground before: $before; foreground after: $after" }
    $ok = Test-Arm64Foreground
    if ($popup -and -not $ok) { throw "Arm64 foreground probe failed after popup dismissal. foreground before: $before; foreground after: $after" }
    if (-not $ok) { Write-Warning 'Foreground probe failed without the popup; continuing under the #2027 ruling, with native checks unchanged.' }
}
