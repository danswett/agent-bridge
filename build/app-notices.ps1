# Distribution notices and their evidence, shared by packing and CI. Reads restored package
# metadata and verbatim notice files; it downloads nothing and makes no compatibility decision.
#Requires -Version 7

function Get-AppNoticePath([string]$Root, [string]$Relative) {
    if (-not $Relative -or [IO.Path]::IsPathRooted($Relative)) { throw "not a relative notice path: $Relative" }
    $rootPath = [IO.Path]::GetFullPath($Root)
    $path = [IO.Path]::GetFullPath((Join-Path $rootPath ($Relative -replace '/', [IO.Path]::DirectorySeparatorChar)))
    $relativePath = [IO.Path]::GetRelativePath($rootPath, $path)
    if ($relativePath -eq '..' -or $relativePath.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -or [IO.Path]::IsPathRooted($relativePath)) {
        throw "notice path escapes its folder: $Relative"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "missing notice file: $path" }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -eq 0) { throw "empty notice file: $path" }
    $current = $path
    while ($true) {
        if ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "linked notice path: $current" }
        if ([IO.Path]::GetRelativePath($rootPath, $current) -eq '.') { break }
        $current = Split-Path $current
    }
    $path
}

function Get-AppNotices([string]$Publish, [string]$Version, [string]$Runtime) {
    $manifestPath = Get-AppNoticePath $Publish 'ThirdPartyNotices/manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $Version -or $manifest.runtime -cne $Runtime -or
        $manifest.commit -cnotmatch '^[0-9a-f]{40}$' -or -not $manifest.files -or -not $manifest.packages) {
        throw 'distribution notice manifest has the wrong schema, build identity or inventory'
    }
    foreach ($required in 'LICENSE.txt', 'SOURCE.txt', 'THIRD-PARTY-NOTICES.txt') {
        if ($manifest.files.path -cnotcontains $required) { throw "distribution notice manifest omits $required" }
    }
    $seen = @{}
    foreach ($file in $manifest.files) {
        if ($seen.ContainsKey($file.path)) { throw "duplicate distribution notice path: $($file.path)" }
        $seen[$file.path] = $true
        $path = Get-AppNoticePath $Publish $file.path
        if ($file.sha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "distribution notice hash mismatch: $($file.path)"
        }
    }
    foreach ($package in $manifest.packages) {
        if (-not $package.files) { throw "package has no bundled notices: $($package.id)/$($package.version)" }
        foreach ($file in $package.files) {
            if ($manifest.files.path -cnotcontains $file) { throw "package notice is not in the file inventory: $file" }
        }
    }
    $manifest
}

function New-AppNotices(
    [string]$RepositoryRoot, [string]$Publish, [string]$Version, [string]$Runtime,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [string]$PackageRoot = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget' 'packages' })
) {
    $noticeRoot = Join-Path $Publish 'ThirdPartyNotices'
    if (Test-Path -LiteralPath $noticeRoot) { throw "notice folder already exists: $noticeRoot; publish into a fresh folder" }
    $deps = @(Get-ChildItem -LiteralPath $Publish -Filter '*.deps.json' -File)
    if (-not $deps) { throw 'published folder has no dependency manifests to identify package notices' }
    $packages = @{}
    $projects = @{}
    foreach ($dep in $deps) {
        $libraries = (Get-Content -LiteralPath $dep.FullName -Raw | ConvertFrom-Json -AsHashtable).libraries
        foreach ($key in $libraries.Keys) {
            if ($libraries[$key].type -notin 'package', 'runtimepack', 'project') { continue }
            $parts = ($key -replace '^runtimepack\.', '') -split '/'
            if ($parts.Count -ne 2 -or $parts[0] -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]*$' -or $parts[1] -cnotmatch '^[0-9][A-Za-z0-9.+-]*$') {
                throw "invalid package identity in $($dep.Name): $key"
            }
            $identity = "$($parts[0])/$($parts[1])".ToLowerInvariant()
            if ($libraries[$key].type -eq 'project') {
                $projects[$identity] = [ordered]@{ id = $parts[0]; version = $parts[1] }
            } else {
                $packages[$identity] = $parts
            }
        }
    }
    if (-not $packages.Count) { throw 'published dependency manifests identify no external packages' }
    New-Item -ItemType Directory -Path $noticeRoot | Out-Null
    $files = [Collections.Generic.List[string]]::new()
    $inventory = [Collections.Generic.List[object]]::new()
    $index = [Collections.Generic.List[string]]::new()
    $index.Add('Third-party notices for this build; individual terms remain separate from the project licence.')
    $index.Add('This inventory does not certify licence compatibility or exhaust native/installer redistribution requirements.')
    foreach ($key in @($packages.Keys | Sort-Object)) {
        $id, $packageVersion = $packages[$key]
        $folder = Join-Path $PackageRoot $id.ToLowerInvariant() $packageVersion.ToLowerInvariant()
        $nuspec = Get-AppNoticePath $folder "$($id.ToLowerInvariant()).nuspec"
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($nuspec, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        } finally { $reader.Dispose() }
        $metadata = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $idNode = if ($metadata) { $metadata.SelectSingleNode('*[local-name()="id"]') } else { $null }
        $versionNode = if ($metadata) { $metadata.SelectSingleNode('*[local-name()="version"]') } else { $null }
        if (-not $idNode -or -not $versionNode -or $idNode.InnerText -ine $id -or $versionNode.InnerText -cne $packageVersion) {
            throw "restored package metadata does not identify $id/$packageVersion"
        }
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        $licenseType = if ($license) { $license.GetAttribute('type') } else { '' }
        $licenseValue = if ($license) { $license.InnerText.Trim() } else { '' }
        $licenseUrlNode = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        $licenseUrl = if ($licenseUrlNode) { $licenseUrlNode.InnerText.Trim() } else { '' }
        $legal = @(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object {
                $_.Name -match '^(licen[cs]e|copying|copyright|notice|third[-_. ]?party)' -and
                $_.Extension -in '', '.txt', '.md', '.html', '.htm', '.rtf', '.json', '.xml', '.pdf'
            } | ForEach-Object FullName)
        if ($licenseType -eq 'file') { $legal += Get-AppNoticePath $folder $licenseValue }
        $legal = @($legal | Sort-Object -Unique)
        if (-not $legal) { throw "$id/$packageVersion has no restored notice files; supply its exact-version copyright/licence material before packing" }
        if (-not $licenseValue) { Write-Warning "$id/$packageVersion has no declared licence; its bundled notices are not licence clearance" }
        $packageFiles = @(foreach ($source in $legal) {
                $relative = [IO.Path]::GetRelativePath($folder, $source) -replace '\\', '/'
                $source = Get-AppNoticePath $folder $relative
                $destination = "ThirdPartyNotices/packages/$id/$packageVersion/$relative"
                $target = Join-Path $Publish ($destination -replace '/', [IO.Path]::DirectorySeparatorChar)
                New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                Copy-Item -LiteralPath $source -Destination $target
                $files.Add($destination)
                $destination
            })
        $inventory.Add([ordered]@{
                id = $id; version = $packageVersion; licenseType = $licenseType; licenseValue = $licenseValue
                licenseUrl = $licenseUrl; files = $packageFiles
            })
        $index.Add("`n$id/$packageVersion")
        $index.Add("NuGet licence declaration: $(if ($licenseValue) { "$licenseType : $licenseValue" } else { 'UNKNOWN' })")
        if ($licenseUrl) { $index.Add("Package-declared licence URL: $licenseUrl") }
        foreach ($file in $packageFiles) { $index.Add("Bundled verbatim: $file") }
    }
    Copy-Item -LiteralPath (Get-AppNoticePath $RepositoryRoot 'LICENSE') -Destination (Join-Path $Publish 'LICENSE.txt')
    $commit = $Commit.ToLowerInvariant()
    @(
        "Slipgates $Version ($Runtime)"
        "Source commit: $commit"
        "Corresponding project source: https://github.com/danswett/agent-bridge/tree/$commit"
        "Source archive: https://github.com/danswett/agent-bridge/archive/$commit.zip"
        "Build and installation material: https://github.com/danswett/agent-bridge/blob/$commit/docs/app-windows.md"
        "Build script: https://github.com/danswett/agent-bridge/blob/$commit/build/pack-app.ps1"
        'These links identify the checked-out commit; uncommitted local changes are not represented.'
        'Project licence: LICENSE.txt. Third-party terms: THIRD-PARTY-NOTICES.txt and ThirdPartyNotices/.'
        'This identifies the project source; it is not a licence-compatibility ruling or a reproducibility certificate.'
    ) | Set-Content -LiteralPath (Join-Path $Publish 'SOURCE.txt') -Encoding utf8NoBOM
    $index | Set-Content -LiteralPath (Join-Path $Publish 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
    foreach ($file in 'LICENSE.txt', 'SOURCE.txt', 'THIRD-PARTY-NOTICES.txt') { $files.Add($file) }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Publish 'Assets' 'Fonts') -Filter '*OFL.txt' -File) {
        $files.Add(([IO.Path]::GetRelativePath($Publish, $file.FullName) -replace '\\', '/'))
    }
    [ordered]@{
        schemaVersion = 1; version = $Version; runtime = $Runtime; commit = $commit
        packages = $inventory.ToArray()
        projects = @($projects.Values | Sort-Object id, version)
        files = @(foreach ($file in @($files | Sort-Object -Unique)) {
                $path = Get-AppNoticePath $Publish $file
                [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
            })
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $noticeRoot 'manifest.json') -Encoding utf8NoBOM
}

function Add-AppSbomLicences([string]$Publish, [string]$Version, [string]$Runtime, [string]$Sbom) {
    $manifest = Get-AppNotices $Publish $Version $Runtime
    $packages = @{}
    foreach ($package in $manifest.packages) { $packages["$($package.id)/$($package.version)".ToLowerInvariant()] = $package }
    $projects = @{}
    foreach ($project in $manifest.projects) { $projects["$($project.id)/$($project.version)".ToLowerInvariant()] = $project }
    $bom = Get-Content -LiteralPath $Sbom -Raw | ConvertFrom-Json -AsHashtable
    if ($bom.bomFormat -cne 'CycloneDX' -or -not $bom.components) { throw 'expected a populated CycloneDX SBOM' }
    $matched = 0
    foreach ($component in $bom.components) {
        if ($component.purl -and $component.purl -cnotmatch '^pkg:nuget/') { continue }
        $name = $component.name -replace '^runtimepack\.', ''
        $key = "$name/$($component.version)".ToLowerInvariant()
        if ($projects.ContainsKey($key)) {
            $matched++
            if (-not $component.licenses) {
                $path = Get-AppNoticePath $Publish 'LICENSE.txt'
                $component.licenses = @(@{ license = @{
                            name = 'Project licence (LICENSE.txt)'
                            text = @{ contentType = 'text/plain'; encoding = 'base64'; content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }
                        } })
            }
            continue
        }
        if (-not $packages.ContainsKey($key)) {
            if (-not $component.licenses) { Write-Warning "SBOM licence remains UNKNOWN: $key; no matching distribution identity" }
            continue
        }
        $matched++
        if ($component.licenses) { continue }
        $package = $packages[$key]
        if ($package.licenseType -eq 'expression' -and $package.licenseValue) {
            $component.licenses = @(@{ expression = $package.licenseValue })
        } elseif ($package.licenseType -eq 'file') {
            $packageRoot = Join-Path $Publish "ThirdPartyNotices/packages/$($package.id)/$($package.version)"
            $path = Get-AppNoticePath $packageRoot $package.licenseValue
            $file = [IO.Path]::GetRelativePath($Publish, $path) -replace '\\', '/'
            if ($package.files -inotcontains $file) { throw "declared licence file is not in this package's notice inventory: $file" }
            $contentType = switch ([IO.Path]::GetExtension($path).ToLowerInvariant()) {
                { $_ -in '', '.txt' } { 'text/plain'; break }
                '.md' { 'text/markdown'; break }
                { $_ -in '.html', '.htm' } { 'text/html'; break }
                '.rtf' { 'application/rtf'; break }
                '.pdf' { 'application/pdf'; break }
                default { 'application/octet-stream' }
            }
            $component.licenses = @(@{ license = @{
                        name = "$($package.id) $($package.version) package licence"
                        text = @{ contentType = $contentType; encoding = 'base64'; content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }
                    } })
        } else {
            Write-Warning "SBOM licence remains UNKNOWN: $key; no licence expression or declared file"
        }
    }
    if (-not $matched) { throw 'no distribution identities matched the SBOM; licence enrichment is unverified' }
    $bom | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Sbom -Encoding utf8NoBOM
}

function Write-AppDistributionEvidence(
    [string]$Publish, [string]$Releases, [string]$Version, [string]$Runtime,
    [string]$Installed, [string]$Out
) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $manifest = Get-AppNotices $Publish $Version $Runtime
    $null = Get-AppNotices $Installed $Version $Runtime
    $manifestName = 'ThirdPartyNotices/manifest.json'
    $manifestHash = (Get-FileHash -LiteralPath (Get-AppNoticePath $Publish $manifestName) -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath (Get-AppNoticePath $Installed $manifestName) -Algorithm SHA256).Hash -cne $manifestHash) {
        throw 'installed notices differ from this build; installer evidence cannot be reused'
    }
    $target = Get-AppChannel $Runtime
    $archives = @(foreach ($kind in 'Portable', 'Full') {
            $name = Get-AppAssetName $target.Channel $kind $Version
            $path = Join-Path $Releases $name
            $archive = [IO.Compression.ZipFile]::OpenRead($path)
            try {
                $prefix = if ($kind -eq 'Portable') { 'current/' } else { 'lib/app/' }
                foreach ($file in @($manifest.files.path) + $manifestName) {
                    $entries = @($archive.Entries | Where-Object FullName -CEQ "$prefix$file")
                    if ($entries.Count -ne 1) { throw "$name has missing or duplicate notice: $file" }
                    $stream = $entries[0].Open()
                    try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                    finally { $stream.Dispose() }
                    $expected = (Get-FileHash -LiteralPath (Get-AppNoticePath $Publish $file) -Algorithm SHA256).Hash
                    if ($actual -cne $expected) { throw "$name has altered notice: $file" }
                }
                [ordered]@{
                    name = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                    files = @($archive.Entries | Where-Object Name | Sort-Object FullName | ForEach-Object {
                            [ordered]@{ path = $_.FullName; size = $_.Length }
                        })
                }
            } finally { $archive.Dispose() }
        })
    $setup = Join-Path $Releases (Get-AppAssetName $target.Channel 'Installer' $Version)
    [ordered]@{
        schemaVersion = 1; version = $Version; runtime = $Runtime; commit = $manifest.commit
        installer = @{ name = (Split-Path $setup -Leaf); sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() }
        archives = $archives
        installedFiles = @(Get-ChildItem -LiteralPath $Installed -Recurse -File | Sort-Object FullName | ForEach-Object {
                [ordered]@{
                    path = ([IO.Path]::GetRelativePath($Installed, $_.FullName) -replace '\\', '/')
                    size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            })
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Out -Encoding utf8NoBOM
}
