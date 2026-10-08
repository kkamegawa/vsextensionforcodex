param(
    [Parameter(Mandatory = $true)][string]$SchemaDirectory,
    [string]$ManifestPath = 'app-server-contract.json',
    [string]$RegressionSchemaDirectory = 'schemas/0.155.1/stable'
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json

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
    @{ Name = 'client request'; Expected = @($manifest.usedMethods.clientRequests); Actual = $clientRequests },
    @{ Name = 'server request'; Expected = @($manifest.usedMethods.serverRequests); Actual = $serverRequests },
    @{ Name = 'server notification'; Expected = @($manifest.usedMethods.serverNotifications); Actual = $serverNotifications }
)) {
    foreach ($method in $definition.Expected) {
        if (-not $definition.Actual.ContainsKey($method)) {
            throw "Used $($definition.Name) method '$method' is absent from $SchemaDirectory."
        }
    }
}

foreach ($method in @($manifest.usedMethods.experimentalDescribedServerNotifications)) {
    if (-not $serverNotifications.ContainsKey($method)) {
        throw "Experimental server notification '$method' is absent from $SchemaDirectory."
    }

    $description = Get-MethodDescription -Path (Join-Path $SchemaDirectory 'ServerNotification.json') -Method $method
    if ($description -notmatch '^EXPERIMENTAL\b') {
        throw "Experimental server notification '$method' is not marked EXPERIMENTAL in $SchemaDirectory."
    }
}

if (-not [string]::IsNullOrWhiteSpace($RegressionSchemaDirectory) -and (Test-Path -LiteralPath $RegressionSchemaDirectory -PathType Container)) {
    $regressionFixtureName = Split-Path -Leaf (Split-Path -Parent $RegressionSchemaDirectory)
    $regressionExpectedMethods = $manifest.regressionMethods.$regressionFixtureName
    if ($null -eq $regressionExpectedMethods) {
        throw "Contract manifest does not define regressionMethods.$regressionFixtureName."
    }

    $regressionClientRequests = Get-MethodTable -Path (Join-Path $RegressionSchemaDirectory 'ClientRequest.json')
    $regressionServerRequests = Get-MethodTable -Path (Join-Path $RegressionSchemaDirectory 'ServerRequest.json')
    $regressionServerNotifications = Get-MethodTable -Path (Join-Path $RegressionSchemaDirectory 'ServerNotification.json')
    foreach ($definition in @(
        @{ Name = 'client request'; Expected = @($regressionExpectedMethods.clientRequests); Actual = $regressionClientRequests },
        @{ Name = 'server request'; Expected = @($manifest.usedMethods.serverRequests); Actual = $regressionServerRequests },
        @{ Name = 'server notification'; Expected = @($regressionExpectedMethods.serverNotifications); Actual = $regressionServerNotifications }
    )) {
        foreach ($method in $definition.Expected) {
            if (-not $definition.Actual.ContainsKey($method)) {
                throw "Used $($definition.Name) method '$method' is absent from regression fixture $RegressionSchemaDirectory."
            }
        }
    }

    foreach ($method in @($manifest.usedMethods.experimentalDescribedServerNotifications)) {
        if (-not $regressionServerNotifications.ContainsKey($method)) {
            throw "Experimental server notification '$method' is absent from regression fixture $RegressionSchemaDirectory."
        }

        $description = Get-MethodDescription -Path (Join-Path $RegressionSchemaDirectory 'ServerNotification.json') -Method $method
        if ($description -notmatch '^EXPERIMENTAL\b') {
            throw "Experimental server notification '$method' is not marked EXPERIMENTAL in regression fixture $RegressionSchemaDirectory."
        }
    }

    Write-Host "Used app-server methods and experimental annotations match the generated contract surface and regression fixture $RegressionSchemaDirectory."
    return
}

Write-Host "Used app-server methods match the generated contract surface in $SchemaDirectory."
