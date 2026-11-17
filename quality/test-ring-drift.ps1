$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$ci = Get-Content "$root/.github/workflows/ci.yml" -Raw
if ($ci -notmatch "(?m)^  schedule:" -or $ci -notmatch "github.event_name != 'schedule'") { throw 'Daily schedule must be guarded to main' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ring-drift-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$saved = @{}; 'EVENT','DRAFT','GITHUB_OUTPUT','GITHUB_STEP_SUMMARY' | ForEach-Object { $saved[$_] = [Environment]::GetEnvironmentVariable($_) }
try {
    $env:EVENT = 'schedule'; $env:DRAFT = ''; $env:GITHUB_OUTPUT = "$tmp/outputs"; $env:GITHUB_STEP_SUMMARY = "$tmp/summary"
    $body = [regex]::Match($ci, '(?ms)- name: Whole ring,.*?        run: \|\r?\n(.*?)(?=      - name:)').Groups[1].Value -replace '(?m)^          ', ''
    function git { $global:LASTEXITCODE = 0 }
    & ([scriptblock]::Create($body))
    Remove-Item Function:git
    if ((Get-Content $env:GITHUB_OUTPUT) -notcontains 'whole-ring=true') { throw 'Schedule did not select whole ring' }
    Remove-Item Env:GITHUB_OUTPUT,Env:GITHUB_STEP_SUMMARY
    $plan = & "$PSScriptRoot/plan.ps1" -Ring 2 -Base HEAD -Head HEAD -Jobs product,app,app-installer
    foreach ($line in 'app=true','app-installer=true','product=true','product-os=["ubuntu-latest","windows-latest","windows-11-arm"]') {
        if ($plan -notcontains $line) { throw "Full scheduled plan missing $line" }
    }
    $script:posts = @(); $script:conclusion = 'failure'; $script:existing = $false
    function gh {
        $global:LASTEXITCODE = 0
        $path = "$args"
        if ($path -match 'issues/2567/comments.*POST') { $script:posts += $args; return '{}' }
        if ($path -match '/comments') { if ($script:existing) { return '[[{"body":"<!-- ring-drift:42 -->"}]]' }; return '[]' }
        if ($path -match '/logs') { return 'Runner image: windows11 20261112.217.1' }
        if ($path -match '/jobs') { return '[{"jobs":[{"id":7,"name":"app (win-arm64)","conclusion":"failure","html_url":"https://example.test/job/7"}]}]' }
        if ($path -match '/runs/42') { return "{`"event`":`"schedule`",`"head_branch`":`"main`",`"conclusion`":`"$script:conclusion`",`"created_at`":`"2026-11-17T12:17:00Z`",`"html_url`":`"https://example.test/run/42`"}" }
        return '{}'
    }
    . "$PSScriptRoot/ring-drift.ps1" -Repository danswett/agent-bridge -RunId 42
    if ($script:posts.Count -eq 0 -or "$script:posts" -notmatch 'app \(win-arm64\).*20261112.217.1') { throw 'Missing failure/image comment payload' }
    $script:posts = @(); $script:conclusion = 'success'
    . "$PSScriptRoot/ring-drift.ps1" -Repository danswett/agent-bridge -RunId 42
    if ($script:posts.Count) { throw 'Passing run posted a comment' }
    $script:conclusion = 'failure'; $script:existing = $true
    . "$PSScriptRoot/ring-drift.ps1" -Repository danswett/agent-bridge -RunId 42
    if ($script:posts.Count) { throw 'Repeated run posted a duplicate comment' }
    '9 scheduled-ring and mocked report checks passed'
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item $tmp -Recurse -Force
}
