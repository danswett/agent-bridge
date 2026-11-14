Describe 'Wake watcher restart progress' {
    BeforeAll {
        $watcher = Join-Path $PSScriptRoot 'wake-watch.ps1'
        function New-Comment([long]$Id, [string]$At, [string]$Body = '[to:watch-test] act') {
            @{ id = $Id; updated_at = $At; body = $Body
                html_url = "https://github.com/example/repo/issues/1#issuecomment-$Id"
                user = @{ login = 'test' } }
        }
        function gh {
            $call = @($args)
            $path = $call[2]
            [void]$global:WakeWatchFixture.calls.Add(($call -join ' '))
            $path | Should -Match 'issues/comments\?since='
            $since = [DateTimeOffset]::Parse(($path -split 'since=|&')[1])
            $page = [int]($path -split 'page=')[-1]
            if ($page -eq $global:WakeWatchFixture.failPage) {
                return "HTTP/2.0 500`n`n{}"
            }
            $items = @($global:WakeWatchFixture.comments | Where-Object {
                [DateTimeOffset]::Parse($_.updated_at) -ge $since
            } | Select-Object -Skip (($page - 1) * 100) -First 100)
            $json = ConvertTo-Json -InputObject $items -Depth 5 -Compress
            $etag = '"' + $json.GetHashCode() + '"'
            if ($global:WakeWatchFixture.blockSave) {
                $state = Get-ChildItem (Join-Path $HOME '.copilot/wake-watch') -Filter '*.json'
                [IO.File]::Delete($state.FullName)
                [void][IO.Directory]::CreateDirectory($state.FullName)
                $global:WakeWatchFixture.blockSave = $false
            }
            if ($call -contains "If-None-Match: $etag") { return "HTTP/2.0 304`n`n" }
            "HTTP/2.0 200`nETag: $etag`n`n$json"
        }
        function Watch([hashtable]$Options = @{}) {
            $parameters = @{ Team = 'watch-test'; MaxMinutes = 1; CommentSeconds = 0 }
            foreach ($key in $Options.Keys) { $parameters[$key] = $Options[$key] }
            $out = (& $watcher @parameters 6>&1) -join "`n"
            $global:WakeWatchFixture.exitCode = $LASTEXITCODE
            $out
        }
    }
    BeforeEach {
        $oldHome = $HOME
        Set-Variable HOME -Value (Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))) -Force
        $global:WakeWatchFixture = @{
            clock = [datetime]::new(2026, 11, 6, 0, 0, 0, [DateTimeKind]::Utc)
            comments = @(); calls = [Collections.Generic.List[string]]::new(); failPage = 0; blockSave = $false
        }
        Mock Get-Date {
            $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddSeconds(10)
            $global:WakeWatchFixture.clock
        }
        Mock Start-Sleep {}
    }
    AfterEach {
        Set-Variable HOME -Value $oldHome -Force
        Remove-Variable WakeWatchFixture -Scope Global
    }

    It 'caps a run without -Async at 60 seconds and says run me async' {
        $out = Watch @{ MaxMinutes = 120 }

        $out | Should -Match '(?m)^.*run me async.*\r?\nwatching comments'
        $out | Should -Match 'WAKE timeout after 1 min .*run me async'
        $out | Should -Not -Match 'timeout after 120 min'
        $global:WakeWatchFixture.calls.Count | Should -BeLessThan 5
    }

    It '-Async keeps the full MaxMinutes and prints no warning' {
        $out = Watch @{ MaxMinutes = 120; Async = $true }

        $out | Should -Match 'WAKE timeout after 120 min'
        $out | Should -Not -Match 'run me async'
        $global:WakeWatchFixture.calls.Count | Should -BeGreaterThan 100
    }

    It 'does not extend a shorter run without -Async' {
        Watch @{ MaxMinutes = 0 } | Should -Match 'WAKE timeout after 0 min'
        $global:WakeWatchFixture.calls.Count | Should -Be 0
    }

    It 'wakes for a comment arriving between two runs without Since or IgnoreIds' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
        $global:WakeWatchFixture.comments += New-Comment 2 '2026-11-06T00:02:00Z'
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        $out = Watch
        $out | Should -Match 'WAKE comment .*issuecomment-2 '
        $out | Should -Not -Match 'WAKE comment .*issuecomment-1 '
    }

    It 'keeps the boundary inclusive without repeating a handled revision' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
        $global:WakeWatchFixture.comments += New-Comment 2 '2026-11-06T00:00:50Z'
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        $out = Watch
        $out | Should -Match 'WAKE comment .*issuecomment-2 '
        $out | Should -Not -Match 'WAKE comment .*issuecomment-1 '
    }

    It 'does not repeat handled comments during unchanged polls after restart' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        $out = Watch
        $out | Should -Match 'WAKE timeout'
        $out | Should -Not -Match 'WAKE comment'
    }

    It 'wakes again when a handled comment is edited while stopped' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
        $global:WakeWatchFixture.comments[0].updated_at = '2026-11-06T00:02:00Z'
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
    }

    It 'retains progress through an empty timeout and uses conditional reads' {
        Watch | Should -Match 'WAKE timeout'
        ($global:WakeWatchFixture.calls -join "`n") | Should -Match 'If-None-Match:'
        $global:WakeWatchFixture.comments = @(
            New-Comment 1 $global:WakeWatchFixture.clock.AddSeconds(1).ToString('yyyy-MM-ddTHH:mm:ssZ'))
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
    }

    It 'does not advance past comments when a later page fails' {
        $global:WakeWatchFixture.comments = @(1..100 | ForEach-Object {
            New-Comment $_ '2026-11-06T00:01:00Z' 'unrelated'
        }) + @(New-Comment 101 '2026-11-06T00:01:01Z')
        $global:WakeWatchFixture.failPage = 2
        Watch | Should -Match 'watch error .*HTTP 500'
        $global:WakeWatchFixture.failPage = 0
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        Watch | Should -Match 'WAKE comment .*issuecomment-101 '
    }

    It 'allows an explicit Since to replay history instead of using saved progress' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        Watch | Should -Match 'WAKE comment .*issuecomment-1 '
        $global:WakeWatchFixture.clock = $global:WakeWatchFixture.clock.AddMinutes(5)

        Watch @{ Since = '2026-11-06T00:00:00Z' } | Should -Match 'WAKE comment .*issuecomment-1 '
    }

    It 'recovers from corrupt startup state in memory without throwing or retrying' {
        Watch | Should -Match 'WAKE timeout'
        $state = Get-ChildItem (Join-Path $HOME '.copilot/wake-watch') -Filter '*.json'
        Set-Content -LiteralPath $state.FullName -Value 'not json'
        $global:WakeWatchFixture.comments = @(
            New-Comment 1 $global:WakeWatchFixture.clock.AddSeconds(20).ToString('yyyy-MM-ddTHH:mm:ssZ'))

        $out = Watch
        $out | Should -Match ([regex]::Escape($state.FullName))
        $out | Should -Match 'STATE WARNING.*-Since'
        $out | Should -Match 'WAKE comment .*issuecomment-1 '
        ([regex]::Matches($out, 'STATE WARNING')).Count | Should -Be 1
        $global:WakeWatchFixture.exitCode | Should -Be 0
        Get-Content -LiteralPath $state.FullName | Should -Be 'not json'
        Watch @{ Since = '2026-11-06T00:00:00Z' } | Should -Match 'WAKE comment .*issuecomment-1 '
    }

    It 'recovers from unreadable startup state in memory without retrying reads (<ReadOperation>)' -TestCases @(
        @{ ReadOperation = 'Get-Content' }, @{ ReadOperation = 'Test-Path' }
    ) {
        param($ReadOperation)
        Watch | Should -Match 'WAKE timeout'
        $state = Get-ChildItem (Join-Path $HOME '.copilot/wake-watch') -Filter '*.json'
        $global:WakeWatchFixture.unreadablePath = $state.FullName
        Mock $ReadOperation { throw [UnauthorizedAccessException]::new('denied') } -ParameterFilter {
            $LiteralPath -eq $global:WakeWatchFixture.unreadablePath
        }
        $global:WakeWatchFixture.comments = @(
            New-Comment 1 $global:WakeWatchFixture.clock.AddSeconds(20).ToString('yyyy-MM-ddTHH:mm:ssZ'))

        $out = Watch
        $out | Should -Match ([regex]::Escape($state.FullName))
        $out | Should -Match 'STATE WARNING.*-Since'
        $out | Should -Match 'WAKE comment .*issuecomment-1 '
        ([regex]::Matches($out, 'STATE WARNING')).Count | Should -Be 1
        $global:WakeWatchFixture.exitCode | Should -Be 0
    }

    It 'warns once and still wakes when state is unwritable at startup' {
        $blocked = Join-Path $HOME 'blocked-state'
        [void][IO.Directory]::CreateDirectory($blocked)
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')

        $out = Watch @{ StatePath = $blocked; Since = '2026-11-06T00:00:00Z' }
        $out | Should -Match ([regex]::Escape($blocked))
        $out | Should -Match 'STATE WARNING.*-Since'
        $out | Should -Match 'WAKE comment .*issuecomment-1 '
        ([regex]::Matches($out, 'STATE WARNING')).Count | Should -Be 1
        $global:WakeWatchFixture.exitCode | Should -Be 0
    }

    It 'does not hide a real wake when saving progress fails after a read' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        $global:WakeWatchFixture.blockSave = $true

        $out = Watch
        $out | Should -Match 'WAKE comment .*issuecomment-1 '
        $out | Should -Match 'STATE WARNING.*-Since'
        $global:WakeWatchFixture.exitCode | Should -Be 0
    }

    It 'does not acknowledge a comment before its wake is announced' {
        $global:WakeWatchFixture.comments = @(New-Comment 1 '2026-11-06T00:00:50Z')
        $out = & $watcher -Team watch-test -MaxMinutes 1 -CommentSeconds 0 | ForEach-Object {
            if ($_ -match '^WAKE comment') {
                $state = Get-ChildItem (Join-Path $HOME '.copilot/wake-watch') -Filter '*.json'
                $saved = Get-Content -LiteralPath $state.FullName -Raw | ConvertFrom-Json -AsHashtable
                $global:WakeWatchFixture.ackBeforeWake = $saved.handled.ContainsKey('1') -or
                    [DateTimeOffset]$saved.since -gt [DateTimeOffset]'2026-11-06T00:00:50Z'
            }
            $_
        }

        ($out -join "`n") | Should -Match 'WAKE comment .*issuecomment-1 '
        $global:WakeWatchFixture.ackBeforeWake | Should -BeFalse
    }
}
