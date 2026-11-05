# Checks quality/lab/provision.ps1, the clean-VM lab's provisioning draft, against a stand-in for
# az: nothing reaches Azure, and no file is written.
#
#   pwsh quality/lab/test-provision.ps1
#
# What it guards: planning calls nothing in Azure; every planned command names the one explicit
# subscription, the lab's one resource group and the run's own resources, and nothing that would
# open the VM or grant anything; resources expire within 4 hours; win-arm64 is native Arm64; bad
# input is refused; and -Apply refuses before any Azure call while its prerequisites are unmet.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$provision = Join-Path $PSScriptRoot 'provision.ps1'
$subscription = '11111111-2222-3333-4444-555555555555'
$group = 'rg-agent-bridge-lab'

# --- Stand-in. A function comes before the program, so the script under test finds this one.
# What it records is global: $script: in it would be the script under test's.
$global:LabAzCalls = [Collections.Generic.List[string]]::new()
function global:az {
    $global:LabAzCalls.Add("az $($args -join ' ')")
    $global:LASTEXITCODE = 0
}

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    $global:LabAzCalls.Clear()
    try { & $body; Write-Output "ok   $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Get-After([object[]]$argv, [string]$flag) {
    $i = [array]::IndexOf($argv, $flag)
    if ($i -lt 0) { return $null }
    $argv[$i + 1]
}
function Test-Refused([hashtable]$arguments) {
    try { & $provision @arguments | Out-Null } catch { return $_.Exception.Message }
    return $null
}

Check 'Planning calls nothing in Azure' {
    foreach ($role in 'win-x64', 'win-arm64') {
        $plan = & $provision -Subscription $subscription -Role $role -RunId 'run20261103'
        Assert ($plan.mode -eq 'plan' -and $plan.draft) "$role mode: $($plan.mode)"
        Assert ($plan.subscription -ceq $subscription) "$role subscription: $($plan.subscription)"
    }
    Assert ($global:LabAzCalls.Count -eq 0) "az ran: $($global:LabAzCalls -join '; ')"
}

Check "Every planned command stays inside the run's own resources in the lab group" {
    $plan = & $provision -Subscription $subscription -Role 'win-x64' -RunId 'abc123'
    Assert (@($plan.resources).Count -eq 3) "resources: $($plan.resources -join ', ')"
    foreach ($name in $plan.resources) { Assert ($name -cmatch '^(disk|nic|vm)-lab-x64-abc123$') "not run-owned: $name" }
    foreach ($argv in @($plan.create) + @($plan.cleanup)) {
        $line = $argv -join ' '
        Assert (@($argv | Where-Object { $_ -eq '--subscription' }).Count -eq 1) "subscription named other than once: $line"
        Assert ((Get-After $argv '--subscription') -ceq $subscription) "another subscription: $line"
        Assert ((Get-After $argv '-g') -ceq $group) "another group: $line"
        Assert ((Get-After $argv '-n') -cin @($plan.resources)) "another name: $line"
        foreach ($word in '--public-ip-address', '--nsg', '--admin-password', '--admin-username', '--assign-identity', '--role', '--scope', '--ids', '--custom-data', '--user-data') {
            Assert ($argv -notcontains $word) "$word in: $line"
        }
        Assert ($argv[1] -notin 'account', 'login', 'group', 'role', 'resource') "reaches beyond the run: $line"
    }
    # The cleanup deletes exactly what the plan creates, the VM first.
    $created = @($plan.create | ForEach-Object { Get-After $_ '-n' })
    $deleted = @($plan.cleanup | ForEach-Object { Get-After $_ '-n' })
    Assert (@(Compare-Object $created $deleted).Count -eq 0) "creates $($created -join ', ') but deletes $($deleted -join ', ')"
    Assert ($deleted[0] -ceq 'vm-lab-x64-abc123') "first delete: $($deleted[0])"
    # The source and subnet are in the same subscription and group.
    $vm = @($plan.create | Where-Object { $_[1] -eq 'vm' })[0]
    Assert ((Get-After $vm '--nics') -ceq 'nic-lab-x64-abc123') "VM's NIC: $(Get-After $vm '--nics')"
    $nic = @($plan.create | Where-Object { $_[1] -eq 'network' })[0]
    Assert ((Get-After $nic '--subnet') -clike "/subscriptions/$subscription/resourceGroups/$group/*") "subnet: $(Get-After $nic '--subnet')"
    Assert ($plan.source -clike "/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.Compute/snapshots/*") "source: $($plan.source)"
}

Check 'Resources expire within four hours, and a longer expiry is refused' {
    $plan = & $provision -Subscription $subscription -Role 'win-x64' -RunId 'abc123'
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $styles = [Globalization.DateTimeStyles]::AdjustToUniversal
    $hours = ([DateTime]::Parse($plan.expiresAt, $culture, $styles) - [DateTime]::Parse($plan.createdAt, $culture, $styles)).TotalHours
    Assert ($hours -gt 0 -and $hours -le 4) "expires after $hours hours"
    foreach ($argv in $plan.create) { Assert ($argv -ccontains "expires-at=$($plan.expiresAt)") "untagged: $($argv -join ' ')" }
    foreach ($bad in 0, 5, 24) {
        Assert ($null -ne (Test-Refused @{ Subscription = $subscription; Role = 'win-x64'; RunId = 'abc123'; ExpiryHours = $bad })) "an expiry of $bad hours was planned"
    }
    Assert ($global:LabAzCalls.Count -eq 0) "az ran: $($global:LabAzCalls -join '; ')"
}

Check 'win-arm64 is native Arm64, never an x64 size, and waits for its feasibility receipt' {
    $arm = & $provision -Subscription $subscription -Role 'win-arm64' -RunId 'abc123'
    $disk = @($arm.create | Where-Object { $_[1] -eq 'disk' })[0]
    Assert ((Get-After $disk '--architecture') -ceq 'Arm64') "disk architecture: $(Get-After $disk '--architecture')"
    $vm = @($arm.create | Where-Object { $_[1] -eq 'vm' })[0]
    Assert ((Get-After $vm '--size') -cmatch '^Standard_D\d+p') "size: $(Get-After $vm '--size')"
    Assert (@($arm.unmet | Where-Object { $_ -like 'arm64-feasibility*' }).Count -eq 1) 'Arm64 feasibility is not listed as unmet'
    $x64 = & $provision -Subscription $subscription -Role 'win-x64' -RunId 'abc123'
    Assert (@($x64.unmet | Where-Object { $_ -like 'arm64-feasibility*' }).Count -eq 0) 'x64 waits for the Arm64 receipt'
    Assert ($arm.resources[0] -cne $x64.resources[0]) 'both roles plan the same names'
}

Check 'The subscription is explicit: no default, no placeholder, none written into the script' {
    foreach ($bad in '', 'default', 'not-a-subscription', '00000000-0000-0000-0000-000000000000', "$subscription; az account set") {
        Assert ($null -ne (Test-Refused @{ Subscription = $bad; Role = 'win-x64'; RunId = 'abc123' })) "subscription '$bad' was accepted"
    }
    $source = Get-Content $provision -Raw
    $ids = [regex]::Matches($source, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}') |
        Where-Object { $_.Value -ne '00000000-0000-0000-0000-000000000000' }
    Assert ($null -eq $ids) "the script names a subscription of its own: $($ids -join ', ')"
    Assert ($source -notmatch 'account\s+(set|show)') 'the script reads or sets the CLI default subscription'
    Assert ($global:LabAzCalls.Count -eq 0) "az ran: $($global:LabAzCalls -join '; ')"
}

Check 'An unknown role or a malformed run id is refused' {
    foreach ($role in 'win-x86', 'linux-arm64', 'WIN-X64', '') {
        Assert ($null -ne (Test-Refused @{ Subscription = $subscription; Role = $role; RunId = 'abc123' })) "role '$role' was accepted"
    }
    foreach ($run in 'abc', 'ABC123', 'abc-123', 'abc 123', 'abcdefghijklm', 'abc12&3', '') {
        Assert ($null -ne (Test-Refused @{ Subscription = $subscription; Role = 'win-x64'; RunId = $run })) "run id '$run' was accepted"
    }
    Assert ($global:LabAzCalls.Count -eq 0) "az ran: $($global:LabAzCalls -join '; ')"
}

Check '-Apply refuses before any Azure call and lists every unmet prerequisite' {
    foreach ($role in 'win-x64', 'win-arm64') {
        $message = Test-Refused @{ Subscription = $subscription; Role = $role; RunId = 'abc123'; Apply = $true; Confirm = $false }
        Assert ($null -ne $message) "$role -Apply went ahead"
        Assert ($message -like '*refused before any Azure call*') "$role -Apply failed some other way: $message"
        $expected = @('owner-authorization', 'bootstrap', 's0-snapshot', 'expiry-sweeper', 'run-registration', 'cost-alerts') + @(if ($role -eq 'win-arm64') { 'arm64-feasibility' })
        foreach ($id in $expected) { Assert ($message -like "*- $id (*") "$role -Apply doesn't list ${id}: $message" }
    }
    Assert ($global:LabAzCalls.Count -eq 0) "az ran: $($global:LabAzCalls -join '; ')"
}

Remove-Item Function:\az
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
