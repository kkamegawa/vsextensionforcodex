function Get-CodexSchemaVersions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Manifest)

    if ($null -eq $Manifest.minimumBoundary -or $null -eq $Manifest.releases) {
        throw 'The Codex contract manifest must define releases and minimumBoundary.'
    }

    $boundaryVersion = [string]$Manifest.minimumBoundary.version
    if ([string]::IsNullOrWhiteSpace($boundaryVersion)) {
        throw 'The Codex contract manifest minimumBoundary must define a version.'
    }

    return @($boundaryVersion) + @($Manifest.releases.PSObject.Properties.Name | Where-Object { $_ -ne $boundaryVersion })
}

function Get-CodexReleaseDefinition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($Version)) { return $null }

    $releaseProperty = $Manifest.releases.PSObject.Properties[$Version]
    if ($null -ne $releaseProperty) { return $releaseProperty.Value }

    if ($null -ne $Manifest.minimumBoundary -and $Version -eq [string]$Manifest.minimumBoundary.version) {
        return $Manifest.minimumBoundary
    }

    return $null
}

function Get-CodexMethodVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$Version
    )

    if ($null -ne $Manifest.minimumBoundary -and $Version -eq [string]$Manifest.minimumBoundary.version) {
        return [string]$Manifest.minimumBoundary.schemaEquivalentVersion
    }

    return $Version
}

Export-ModuleMember -Function Get-CodexSchemaVersions, Get-CodexReleaseDefinition, Get-CodexMethodVersion