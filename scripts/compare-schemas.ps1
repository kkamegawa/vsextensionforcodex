param(
    [Parameter(Mandatory = $true)][string]$BaselineDirectory,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('stable', 'experimental')][string]$Surface,
    [string]$ManifestPath = 'app-server-contract.json',
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
function Get-Shape($value) {
    if ($null -eq $value) { return 'null' }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $properties = @($value.psobject.Properties |
            Where-Object { $_.Name -notin @('$schema', 'default', 'description', 'examples', 'markdownDescription', 'title') } |
            Sort-Object Name)
        return '{' + (($properties | ForEach-Object { "$( $_.Name )=$([string](Get-Shape $_.Value))" }) -join ';') + '}'
    }
    if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
        return '[' + (@($value | ForEach-Object { Get-Shape $_ } | Sort-Object) -join ',') + ']'
    }
    return ($value | ConvertTo-Json -Compress)
}
function Get-Index([string]$directory) {
    $directory = (Resolve-Path -LiteralPath $directory).Path
    $index = @{}
    Get-ChildItem -LiteralPath $directory -Filter '*.json' -File -Recurse |
        Where-Object {
            $_.Name -ne '.schema-metadata.json' -and
            $_.Name -notin @('codex_app_server_protocol.schemas.json', 'codex_app_server_protocol.v2.schemas.json')
        } |
        ForEach-Object {
        $relative = $_.FullName.Substring($directory.TrimEnd('\').Length + 1).Replace('\', '/')
        $index[$relative] = Get-Shape (Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json)
    }
    return $index
}
$baseline = Get-Index $BaselineDirectory
$target = Get-Index $TargetDirectory
$names = @($baseline.Keys + $target.Keys | Sort-Object -Unique)
$differences = foreach ($name in $names) {
    if (-not $baseline.ContainsKey($name)) { "added: $name" }
    elseif (-not $target.ContainsKey($name)) { "removed: $name" }
    elseif ($baseline[$name] -ne $target[$name]) { "changed: $name" }
}
if ($ReportPath) { $differences | Set-Content -LiteralPath $ReportPath -Encoding utf8 }
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
$expectedDefinition = $manifest.knownDifferences.$Surface
$expected = @()
foreach ($name in @($expectedDefinition.added)) { $expected += "added: $name" }
foreach ($name in @($expectedDefinition.removed)) { $expected += "removed: $name" }
foreach ($name in @($expectedDefinition.changed)) { $expected += "changed: $name" }
$unexpected = @($differences | Where-Object { $_ -notin $expected })
$missing = @($expected | Where-Object { $_ -notin $differences })
if ($unexpected -or $missing) {
    if ($unexpected) { Write-Error ("Unexpected schema differences: " + ($unexpected -join ', ')) }
    if ($missing) { Write-Error ("Expected schema differences were not observed: " + ($missing -join ', ')) }
    exit 1
}
Write-Host "Schema structures match the $Surface expected-difference contract."
