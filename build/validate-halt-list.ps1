# The publisher and a release maintainer's emergency upload use the client's exact schema.
function Assert-ReleaseHaltList([string]$Path) {
    $size = (Get-Item $Path).Length
    if ($size -le 0 -or $size -gt 65536) { throw 'halt policy must be 1 through 65536 bytes' }
    $options = [System.Text.Json.JsonDocumentOptions]@{ MaxDepth = 4 }
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new([IO.File]::ReadAllBytes($Path)), $options)
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'halt policy must be an object' }
        $names = @($root.EnumerateObject() | ForEach-Object Name)
        if ($names.Count -ne 2 -or $names -cnotcontains 'v' -or $names -cnotcontains 'halted') {
            throw 'halt policy must contain exactly v and halted'
        }
        $version = $root.GetProperty('v')
        $schema = 0
        if ($version.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $version.TryGetInt32([ref]$schema) -or $schema -ne 1) {
            throw 'halt policy v must be the integer 1'
        }
        $halted = $root.GetProperty('halted')
        if ($halted.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $halted.GetArrayLength() -gt 1024) {
            throw 'halt policy halted must be a bounded array'
        }
        foreach ($entry in $halted.EnumerateArray()) {
            if ($entry.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'halted versions must be strings' }
            if ($entry.GetString() -cnotmatch '^0\.1\.(0|[1-9][0-9]{0,18})\z') { throw 'invalid halted version' }
            [long]$run = 0
            if (-not [long]::TryParse($Matches[1], [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$run)) {
                throw 'halted version run exceeds the client range'
            }
        }
    }
    finally { $document.Dispose() }
}
