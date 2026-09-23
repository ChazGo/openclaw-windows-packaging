[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OpenClawDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$applicationDirectory = (Resolve-Path -LiteralPath $OpenClawDirectory).Path
& node (Join-Path $PSScriptRoot 'fixtures\gateway-isolation-context.mjs') $applicationDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Gateway isolation context proof failed with exit code $LASTEXITCODE."
}
