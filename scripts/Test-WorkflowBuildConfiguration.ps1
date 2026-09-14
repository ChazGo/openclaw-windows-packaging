[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workflowPath = Join-Path `
    $repositoryRoot `
    '.github\workflows\gateway-msix.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw

$requiredFragments = @(
    'name: Build OpenClaw'
    'OPENCLAW_CONTROL_UI_RELEASE_BUILD: "1"'
    'run: pnpm build'
)

foreach ($fragment in $requiredFragments) {
    if (-not $workflow.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Build workflow is missing required configuration: $fragment"
    }
}

if ($workflow.Contains('pnpm ui:build', [StringComparison]::Ordinal)) {
    throw (
        'Build workflow must not rebuild the Control UI after pnpm build; ' +
        'a second build creates a different release build identity.'
    )
}

Write-Host 'Gateway MSIX build workflow configuration passed.'
