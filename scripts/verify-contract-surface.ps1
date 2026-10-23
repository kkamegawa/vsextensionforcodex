param(
    [Parameter(Mandatory = $true)][string]$SchemaDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('stable', 'experimental')][string]$Surface,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ManifestPath = 'app-server-contract.json'
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
$expectedMethods = $manifest.methodsByVersion.$Version.$Surface
if ($null -eq $expectedMethods) {
    throw "The contract manifest has no method table for $Version/$Surface."
}

function Get-MethodTable {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $schema = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $methods = @{}
    foreach ($variant in @($schema.oneOf)) {
        $method = @($variant.properties.method.enum)[0]
        if ([string]::IsNullOrWhiteSpace($method)) {
            throw "Contract variant has no exact method enum: $Path"
        }

        if ($methods.ContainsKey($method)) {
            throw "Contract method '$method' is duplicated: $Path"
        }

        $methods[$method] = $variant
    }

    return $methods
}

function Get-MethodDescription {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Method
    )

    $schema = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $variant = @($schema.oneOf | Where-Object { @($_.properties.method.enum) -contains $Method })[0]
    if ($null -eq $variant) {
        return ''
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$variant.description)) {
        return [string]$variant.description
    }

    $reference = [string]$variant.properties.params.'$ref'
    if ($reference -notmatch '^#/') {
        return ''
    }

    $definition = $schema
    foreach ($segment in $reference.Substring(2).Split('/')) {
        $propertyName = $segment.Replace('~1', '/').Replace('~0', '~')
        $definition = $definition.$propertyName
        if ($null -eq $definition) {
            return ''
        }
    }

    return [string]$definition.description
}

$clientRequests = Get-MethodTable -Path (Join-Path $SchemaDirectory 'ClientRequest.json')
$serverRequests = Get-MethodTable -Path (Join-Path $SchemaDirectory 'ServerRequest.json')
$serverNotifications = Get-MethodTable -Path (Join-Path $SchemaDirectory 'ServerNotification.json')

foreach ($definition in @(
    @{ Name = 'client request'; Expected = @($expectedMethods.clientRequests); Actual = $clientRequests },
    @{ Name = 'server request'; Expected = @($expectedMethods.serverRequests); Actual = $serverRequests },
    @{ Name = 'server notification'; Expected = @($expectedMethods.serverNotifications); Actual = $serverNotifications }
)) {
    foreach ($method in $definition.Expected) {
        if (-not $definition.Actual.ContainsKey($method)) {
            throw "Used $($definition.Name) method '$method' is absent from $SchemaDirectory."
        }
    }
}

if ($Surface -eq 'experimental') {
    foreach ($method in @($expectedMethods.experimentalServerNotifications)) {
        if (-not $serverNotifications.ContainsKey($method)) {
            throw "Experimental server notification '$method' is absent from $Version/$Surface."
        }

        $description = Get-MethodDescription -Path (Join-Path $SchemaDirectory 'ServerNotification.json') -Method $method
        if ($description -notmatch '^EXPERIMENTAL\b') {
            throw "Experimental server notification '$method' is not marked EXPERIMENTAL in $Version/$Surface."
        }
    }
}

if ($Version -eq '0.163.0') {
    $readStateUpdate = 'thread/readState/update'
    if ($Surface -eq 'experimental' -and -not $clientRequests.ContainsKey($readStateUpdate)) {
        throw "The experimental 0.163.0 contract is missing $readStateUpdate."
    }
    if ($Surface -eq 'stable' -and $clientRequests.ContainsKey($readStateUpdate)) {
        throw "The stable 0.163.0 contract unexpectedly exposes $readStateUpdate."
    }
    if ($Surface -eq 'experimental' -and @($expectedMethods.serverNotifications) -notcontains 'thread/readState/changed') {
        throw 'The 0.163.0 experimental method table must include thread/readState/changed.'
    }
}

Write-Host "Used app-server methods match the $Version/$Surface generated contract surface in $SchemaDirectory."
