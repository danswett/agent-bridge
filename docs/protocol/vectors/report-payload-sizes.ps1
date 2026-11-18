#Requires -Version 7.4
param([string] $VectorPath = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$options = [System.Text.Json.JsonSerializerOptions]::new()
$options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
$sizes = [System.Collections.Generic.SortedDictionary[string, System.Collections.Generic.List[int]]]::new([StringComparer]::Ordinal)
$files = @(Get-ChildItem -LiteralPath $VectorPath -Filter '*.json' -File)
if ($files.Count -eq 0) { throw "No JSON vector files found in '$VectorPath'." }

foreach ($file in $files) {
    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($file.FullName))
        $records = [System.Text.Json.JsonElement]::new()
        # Handshake, roster and signing fixtures do not use the record-vector schema.
        if (-not $document.RootElement.TryGetProperty('records', [ref] $records)) { continue }
        foreach ($record in $records.EnumerateObject()) {
            if (-not $sizes.ContainsKey($record.Name)) {
                $sizes.Add($record.Name, [System.Collections.Generic.List[int]]::new())
            }
            foreach ($kind in 'valid', 'refused') {
                foreach ($vector in $record.Value.GetProperty($kind).EnumerateArray()) {
                    $payload = $vector.GetProperty('json')
                    $bytes = [System.Text.Json.JsonSerializer]::SerializeToUtf8Bytes[System.Text.Json.JsonElement]($payload, $options)
                    $sizes[$record.Name].Add($bytes.Length)
                }
            }
            if ($sizes[$record.Name].Count -eq 0) { throw "Record '$($record.Name)' has no payload vectors." }
        }
    }
    catch {
        throw "Cannot measure vector file '$($file.Name)': $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
    }
}
if ($sizes.Count -eq 0) { throw "No record payload vectors found in '$VectorPath'." }

"MessageType`tMinBytes`tMaxBytes`tCount"
foreach ($entry in $sizes.GetEnumerator()) {
    $range = $entry.Value | Measure-Object -Minimum -Maximum
    "$($entry.Key)`t$($range.Minimum)`t$($range.Maximum)`t$($entry.Value.Count)"
}
