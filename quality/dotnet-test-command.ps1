function Add-DotnetCrashBlame([string]$Command) {
    if ($Command -match '^\s*dotnet\s+test\b' -and $Command -notmatch '(?:^|\s)--blame-crash(?:\s|$)') {
        return "$Command --blame-crash"
    }
    return $Command
}
