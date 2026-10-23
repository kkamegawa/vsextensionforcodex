param(
    [Parameter(Mandatory = $true)][string]$BaselineDirectory,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('stable', 'experimental')][string]$Surface,
    [Parameter(Mandatory = $true)][string]$BaselineVersion,
    [Parameter(Mandatory = $true)][string]$TargetVersion,
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
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
if ($TargetVersion -ne $manifest.targetVersion) {
    throw "Target version $TargetVersion does not match the contract target $($manifest.targetVersion)."
}
if ($BaselineVersion -notin @($manifest.regressionVersions)) {
    throw "Baseline version $BaselineVersion is not a declared regression version."
}
if ((Split-Path -Leaf (Split-Path -Parent $BaselineDirectory)) -ne $BaselineVersion -or
    (Split-Path -Leaf $BaselineDirectory) -ne $Surface -or
    (Split-Path -Leaf (Split-Path -Parent $TargetDirectory)) -ne $TargetVersion -or
    (Split-Path -Leaf $TargetDirectory) -ne $Surface) {
    throw 'Schema directory names do not match the declared versions and surface.'
}
$expectedDefinition = $manifest.knownDifferences.$BaselineVersion.$Surface
if ($null -eq $expectedDefinition) {
    throw "The contract manifest has no known differences for $BaselineVersion -> $TargetVersion ($Surface)."
}
$expected = @()
foreach ($name in @($expectedDefinition.added)) { $expected += "added: $name" }
foreach ($name in @($expectedDefinition.removed)) { $expected += "removed: $name" }
foreach ($name in @($expectedDefinition.changed)) { $expected += "changed: $name" }
$unexpected = @($differences | Where-Object { $_ -notin $expected })
$missing = @($expected | Where-Object { $_ -notin $differences })
if ($ReportPath) {
    $report = @(
        "baselineVersion: $BaselineVersion",
        "targetVersion: $TargetVersion",
        "surface: $Surface",
        "differences:"
    ) + @($differences)
    $report | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
if ($unexpected -or $missing) {
    if ($unexpected) { Write-Error ("Unexpected schema differences: " + ($unexpected -join ', ')) }
    if ($missing) { Write-Error ("Expected schema differences were not observed: " + ($missing -join ', ')) }
    exit 1
}
Write-Host "Schema structures match the $BaselineVersion -> $TargetVersion $Surface expected-difference contract."
