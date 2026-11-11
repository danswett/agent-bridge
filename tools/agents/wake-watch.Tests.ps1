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
            if ($call -contains "If-None-Match: $etag") { return "HTTP/2.0 304`n`n" }
            "HTTP/2.0 200`nETag: $etag`n`n$json"
        }
        function Watch([hashtable]$Options = @{}) {
            (& $watcher -Team watch-test -MaxMinutes 1 -CommentSeconds 0 @Options 6>&1) -join "`n"
        }
    }
    BeforeEach {
        $oldHome = $HOME
        Set-Variable HOME -Value (Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))) -Force
        $global:WakeWatchFixture = @{
            clock = [datetime]::new(2026, 11, 6, 0, 0, 0, [DateTimeKind]::Utc)
            comments = @(); calls = [Collections.Generic.List[string]]::new(); failPage = 0
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

    It 'reports corrupt state instead of silently resetting the cursor' {
        Watch | Should -Match 'WAKE timeout'
        $state = Get-ChildItem (Join-Path $HOME '.copilot/wake-watch') -Filter '*.json'
        Set-Content -LiteralPath $state.FullName -Value 'not json'

        { Watch } | Should -Throw
    }
}
