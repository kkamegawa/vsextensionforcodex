param(
    [Parameter(Mandatory = $true)][string]$SchemaDirectory,
    [string]$ManifestPath = 'app-server-contract.json'
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

Write-Host "Used app-server methods match the generated contract surface in $SchemaDirectory."
