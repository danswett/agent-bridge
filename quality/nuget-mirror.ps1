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

function New-NuGetMirrorConfig {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Path
    )
    # A feed's key name (azure-default, say) instead of its URL would otherwise reach NuGet as a
    # relative folder, and fail far from the variable that caused it.
    $uri = $null
    if (-not [uri]::TryCreate($Source, [UriKind]::Absolute, [ref] $uri)) {
        throw "AGENT_BRIDGE_NUGET_SOURCE must be a feed's absolute URL or folder, such as https://<mirror>/nuget/v3/index.json, not '$Source'."
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
