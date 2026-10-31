# Says whether a host build is a release build or a dev build: "release" or "dev". The host
# decides the same way at run time (Program.cs, IsDevBuild): a release build carries the
# assembly metadata AgentBridgeRelease=true, which only `-p:AgentBridgeRelease=true` (pack-app.ps1
# -Release) adds. A dev build's Home Assistant connector writes only to a disposable Home
# Assistant (design G6), so only the publish path may make a release build.
#
#   pwsh build/host-identity.ps1 out/app/publish/agent-bridge-host.dll
#   pwsh build/host-identity.ps1 out/app/releases/AgentBridgeApp-0.1.600-full.nupkg
#
# Reads the metadata without loading the assembly, so any .NET can read any host.
#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Path = [IO.Path]::GetFullPath($Path)
$bytes = if ($Path -like '*.nupkg') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('lib/app/agent-bridge-host.dll')
        if (-not $entry) { throw "no lib/app/agent-bridge-host.dll in $Path" }
        $memory = [IO.MemoryStream]::new()
        $stream = $entry.Open()
        try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
        , $memory.ToArray()
    } finally { $zip.Dispose() }
} else {
    , [IO.File]::ReadAllBytes($Path)
}

$pe = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new($bytes))
try {
    $md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    foreach ($handle in $md.GetAssemblyDefinition().GetCustomAttributes()) {
        $attribute = $md.GetCustomAttribute($handle)
        if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
        $constructor = $md.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
        if ($constructor.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
        $type = $md.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
        if ($md.GetString($type.Name) -ne 'AssemblyMetadataAttribute') { continue }
        # The value blob: the 0x0001 prolog, then the key and the value, each a length and UTF-8.
        $blob = $md.GetBlobBytes($attribute.Value)
        $at = 2
        $strings = foreach ($i in 1..2) {
            $length = $blob[$at]
            if ($length -ge 0x80) { $length = $null; break }
            [Text.Encoding]::UTF8.GetString($blob, $at + 1, $length)
            $at += 1 + $length
        }
        if (@($strings).Count -eq 2 -and $strings[0] -eq 'AgentBridgeRelease' -and $strings[1] -eq 'true') { return 'release' }
    }
    'dev'
} finally { $pe.Dispose() }
