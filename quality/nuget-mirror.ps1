<#
.SYNOPSIS
  Writes the NuGet config run.ps1 restores with when AGENT_BRIDGE_NUGET_SOURCE names a mirror (#475).

.DESCRIPTION
  Some machines can't reach api.nuget.org and restore through a mirror instead. Passing the mirror
  with --source isn't enough: it replaces the package sources, but NuGet's vulnerability audit then
  still tries nuget.org from the repository's nuget.config, and its NU1900 warning is an error under
  TreatWarningsAsErrors. So the config lists the mirror as both the only package source and the
  only audit source, which keeps audit on and pointed at the mirror. It is written outside source
  control on purpose: CI and every other machine keep the committed nuget.config.
#>

# The ab-ci runners' mirror (ci.yml product, self-hosted legs only; #4104, #4218). Their .env names it in
# AGENT_BRIDGE_NUGET_SOURCE; this is the one host CI accepts there. nuget.org's repository-signing certificates,
# current and earlier (packages signed under each are still served), by SHA-256: a package must carry one of them.
$script:AbCiMirrorHost = 'packagefeedproxy.microsoft.io'
$script:NuGetOrgRepositoryCertificates = @(
    '1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D'
    '5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4'
    '0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D')

# The parsed mirror URL, or a throw that never echoes the value: https only, the one pinned host, and no user info,
# port, query or fragment. Parsed, not pattern-matched, so a look-alike host or an @-trick can't pass.
function Assert-AbCiMirrorUrl([string] $Source) {
    $uri = $null
    # The text's own authority too: [uri] drops a default port, so https://host:443/ would otherwise pass.
    $authority = if ($Source -match '^https://([^/?#]*)') { $Matches[1] } else { '' }
    $ok = $authority -eq $script:AbCiMirrorHost -and
        [uri]::TryCreate($Source, [UriKind]::Absolute, [ref] $uri) -and $uri.Scheme -ceq 'https' -and
        $uri.Host -eq $script:AbCiMirrorHost -and $uri.UserInfo -eq '' -and $uri.Authority -eq $uri.Host -and
        $uri.Query -eq '' -and $uri.Fragment -eq ''
    if (-not $ok) { throw "AGENT_BRIDGE_NUGET_SOURCE must be https://$($script:AbCiMirrorHost)/... with no user info, port or query; nuget.org is unreachable from this runner, so nothing else restores." }
    $uri
}

# The config an ab-ci leg restores with: the mirror as the only package and audit source, no fallback folders, and
# every package required to carry a nuget.org repository signature, so the mirror can serve nuget.org's own bytes
# or nothing (NU3034). A mirror that is down fails the restore (NU1301): there is no other source.
function New-AbCiNuGetConfig {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Source,
        [Parameter(Mandatory)] [string] $Path
    )
    $uri = Assert-AbCiMirrorUrl $Source
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path)
    $settings = [System.Xml.XmlWriterSettings]@{ Indent = $true; Encoding = [System.Text.UTF8Encoding]::new($false) }
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartElement('configuration')
        $writer.WriteStartElement('config')
        $writer.WriteStartElement('add'); $writer.WriteAttributeString('key', 'signatureValidationMode'); $writer.WriteAttributeString('value', 'require'); $writer.WriteEndElement()
        $writer.WriteEndElement()
        foreach ($section in 'packageSources', 'auditSources') {
            $writer.WriteStartElement($section)
            $writer.WriteStartElement('clear'); $writer.WriteEndElement()
            $writer.WriteStartElement('add'); $writer.WriteAttributeString('key', 'mirror'); $writer.WriteAttributeString('value', $uri.AbsoluteUri); $writer.WriteEndElement()
            $writer.WriteEndElement()
        }
        $writer.WriteStartElement('fallbackPackageFolders'); $writer.WriteStartElement('clear'); $writer.WriteEndElement(); $writer.WriteEndElement()
        $writer.WriteStartElement('trustedSigners')
        $writer.WriteStartElement('repository')
        $writer.WriteAttributeString('name', 'nuget.org')
        $writer.WriteAttributeString('serviceIndex', 'https://api.nuget.org/v3/index.json')
        foreach ($fingerprint in $script:NuGetOrgRepositoryCertificates) {
            $writer.WriteStartElement('certificate')
            $writer.WriteAttributeString('fingerprint', $fingerprint)
            $writer.WriteAttributeString('hashAlgorithm', 'SHA256')
            $writer.WriteAttributeString('allowUntrustedRoot', 'false')
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    finally {
        $writer.Dispose()
    }
}

function New-NuGetMirrorConfig {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Path
    )
    # A feed's key name (azure-default, say) instead of its URL would otherwise reach NuGet as a
    # relative folder, and fail far from the variable that caused it. The value isn't echoed, in
    # case it holds a token.
    $uri = $null
    if (-not [uri]::TryCreate($Source, [UriKind]::Absolute, [ref] $uri)) {
        throw "AGENT_BRIDGE_NUGET_SOURCE must be a feed's absolute URL or folder, such as https://<mirror>/nuget/v3/index.json; a feed's name or a relative path won't do."
    }
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path)
    $settings = [System.Xml.XmlWriterSettings]@{ Indent = $true; Encoding = [System.Text.UTF8Encoding]::new($false) }
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartElement('configuration')
        foreach ($section in 'packageSources', 'auditSources') {
            $writer.WriteStartElement($section)
            $writer.WriteStartElement('clear')
            $writer.WriteEndElement()
            $writer.WriteStartElement('add')
            $writer.WriteAttributeString('key', 'mirror')
            $writer.WriteAttributeString('value', $Source)
            $writer.WriteEndElement()
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    }
    finally {
        $writer.Dispose()
    }
}
