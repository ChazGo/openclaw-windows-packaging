[CmdletBinding()]
param(
    [string]$TestRoot,
    [string]$EvidenceDirectory,
    [string]$ProductionSourceCommit = '9aa1df286c2b6fd59b4c101201ddae0ae6000324'
)

# Do not enable strict mode: Build-Payload.ps1 must exercise its own missing-field semantics.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $TestRoot) {
    $TestRoot = Join-Path $repositoryRoot '.validation-payload-matrix'
}
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $repositoryRoot 'docs\validation\pr-28'
}
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not $TestRoot.StartsWith("$repositoryRoot\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'TestRoot must be a new directory beneath the repository.'
}
if (Test-Path -LiteralPath $TestRoot) {
    throw 'TestRoot already exists. Refusing to overwrite or remove existing data.'
}
if ($EvidenceDirectory.StartsWith("$TestRoot\", [StringComparison]::OrdinalIgnoreCase) -or
    $EvidenceDirectory -eq $TestRoot) {
    throw 'EvidenceDirectory must not be inside the generated fixture directory.'
}
if ($PSVersionTable.PSEdition -ne 'Core' -or -not $IsWindows) {
    throw 'Run this harness with pwsh on Windows.'
}
$nodeVersion = (& node --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $nodeVersion -ne 'v24.16.0') {
    throw 'Node.js v24.16.0 must be selected on PATH.'
}
$npmVersion = (& npm --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'npm --version failed.' }
$head = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve evidence checkout HEAD.' }
$sourceFiles = @(
    'scripts/Build-Payload.ps1',
    'plugins/gateway-isolation/package.json',
    'plugins/gateway-isolation/openclaw.plugin.json',
    'plugins/gateway-isolation/index.js'
)
$sourceHashes = foreach ($relative in $sourceFiles) {
    $path = Join-Path $repositoryRoot $relative.Replace('/', '\')
    $baselineBlob = (& git -C $repositoryRoot rev-parse "${ProductionSourceCommit}:$relative" | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve production source blob.' }
    $currentBlob = (& git -C $repositoryRoot hash-object $path | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $currentBlob -ne $baselineBlob) {
        throw "Production source differs from the selected baseline: $relative"
    }
    [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Assert-Matrix {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-EnvironmentSnapshot {
    param([string[]]$Names)
    $snapshot = @{}
    foreach ($name in $Names) {
        $snapshot[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    return $snapshot
}

function Restore-EnvironmentSnapshot {
    param([hashtable]$Snapshot)
    foreach ($name in $Snapshot.Keys) {
        if ($null -eq $Snapshot[$name]) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($name, $Snapshot[$name], 'Process')
        }
    }
}

function ConvertTo-PublicText {
    param([string]$Text)
    return $Text.Replace($TestRoot, '<TestRoot>').Replace($repositoryRoot, '<RepositoryRoot>')
}

$shapeReason = 'The selected OpenClaw payload did not load the Gateway isolation plugin with the required read-only runtime shape.'
$templates = [Collections.Generic.List[object]]::new()
$templates.Add(@{ id = 'accepted-existing-environment'; kind = 'accepted'; environment = 'existing'; reason = 'accepted' })
$templates.Add(@{ id = 'accepted-absent-environment'; kind = 'accepted'; environment = 'absent'; reason = 'accepted' })
$templates.Add(@{ id = 'accepted-empty-environment'; kind = 'accepted'; environment = 'empty'; reason = 'accepted' })
$templates.Add(@{ id = 'missing-extensions'; kind = 'missing-extensions'; reason = 'The pinned OpenClaw payload does not expose the supported bundled plugin directory: <TestRoot>\<Case>\runner\openclaw-stage-<Architecture>\node_modules\openclaw\dist\extensions' })
$templates.Add(@{ id = 'preexisting-plugin'; kind = 'preexisting-plugin'; reason = 'The OpenClaw payload already contains a gateway-isolation plugin; refusing to replace upstream content.' })
$templates.Add(@{ id = 'inspection-exit-23'; kind = 'exit'; reason = 'The selected OpenClaw payload cannot load the Gateway isolation plugin. Exit code: 23.' })
$fields = [ordered]@{
    'plugin.id' = 'unexpected-plugin'
    'plugin.origin' = 'global'
    'plugin.enabled' = $false
    'plugin.activated' = $false
    'plugin.status' = 'error'
    'plugin.imported' = $false
    'plugin.httpRoutes' = 0
    'httpRouteCount' = 0
    'gatewayMethods' = @('fixture.method')
    'tools' = @('fixture.tool')
    'services' = @('fixture.service')
    'diagnostics' = @('fixture diagnostic')
}
foreach ($field in $fields.Keys) {
    $templates.Add(@{ id = "wrong-$($field.Replace('.', '-'))"; kind = 'wrong'; field = $field; value = $fields[$field]; reason = $shapeReason })
    $templates.Add(@{ id = "missing-$($field.Replace('.', '-'))"; kind = 'missing'; field = $field; reason = $shapeReason })
}
foreach ($field in @('plugin.httpRoutes', 'httpRouteCount')) {
    $templates.Add(@{ id = "excess-$($field.Replace('.', '-'))"; kind = 'wrong'; field = $field; value = 2; reason = $shapeReason })
}
$templates.Add(@{ id = 'missing-plugin-object'; kind = 'missing'; field = 'plugin'; reason = $shapeReason })

$runtimeFixture = @'
import fs from "node:fs";
const args = process.argv.slice(2);
const inspect = JSON.stringify(args) === JSON.stringify(["plugins", "inspect", "gateway-isolation", "--runtime", "--json"]);
const version = JSON.stringify(args) === JSON.stringify(["--version"]);
const record = {
  command: inspect ? "runtime-inspection" : version ? "version-smoke" : "unexpected",
  args,
  nodeVersion: process.version,
  isolationMode: process.env.CLAWCTL_GATEWAY_ISOLATION ?? null,
  stateDirectoryMatchesExpected: process.env.OPENCLAW_STATE_DIR === process.env.PAYLOAD_MATRIX_EXPECTED_STATE_DIR
};
fs.appendFileSync(process.env.PAYLOAD_MATRIX_OBSERVATIONS, JSON.stringify(record) + "\n");
if (version) {
  console.log("2026.8.2-synthetic-fixture");
} else if (inspect) {
  const fixture = JSON.parse(fs.readFileSync(new URL("./inspection-fixture.json", import.meta.url), "utf8"));
  console.log(JSON.stringify(fixture.inspection));
  process.exitCode = fixture.exitCode;
} else {
  process.exitCode = 91;
}
'@

$environmentNames = @(
    'RUNNER_TEMP', 'npm_config_arch', 'npm_config_target_arch',
    'OPENCLAW_STATE_DIR', 'CLAWCTL_GATEWAY_ISOLATION',
    'npm_config_cache', 'npm_config_offline', 'npm_config_update_notifier',
    'PAYLOAD_MATRIX_OBSERVATIONS', 'PAYLOAD_MATRIX_EXPECTED_STATE_DIR'
)
$buildEnvironmentNames = @('npm_config_arch', 'npm_config_target_arch', 'OPENCLAW_STATE_DIR', 'CLAWCTL_GATEWAY_ISOLATION', 'RUNNER_TEMP')
$originalEnvironment = Get-EnvironmentSnapshot $environmentNames
$originalLocation = (Get-Location).Path
$results = [Collections.Generic.List[object]]::new()
$transcript = [Collections.Generic.List[string]]::new()
$transcript.Add('Gateway isolation payload composition matrix')
$transcript.Add('Fixture type: synthetic local npm packages and synthetic runtime-inspection responses.')
$transcript.Add('Real operations: npm pack, npm install, scripts\Build-Payload.ps1, file copy, runtime command invocation, payload metadata.')
$transcript.Add('This matrix tests the x64 and ARM64 composition contract with controlled runtime-inspection fixtures.')
$transcript.Add("Production source: $ProductionSourceCommit")
$transcript.Add("Evidence checkout HEAD: $head")
$transcript.Add("Runtime: pwsh $($PSVersionTable.PSVersion); Node.js $nodeVersion; npm $npmVersion; Windows x64 host")
$transcript.Add('Command: $env:PATH = "<Node-v24.16.0-win-x64>;" + $env:PATH')
$transcript.Add('Command: pwsh -NoProfile -File .\scripts\validation\Test-GatewayIsolationPayloadMatrix.ps1')
$transcript.Add('All npm operations use an isolated generated cache and npm_config_offline=true; packages have no dependencies or lifecycle scripts.')
$createdRoot = $false
try {
    New-Item -Path $TestRoot -ItemType Directory | Out-Null
    $createdRoot = $true
    $env:npm_config_cache = Join-Path $TestRoot 'npm-cache'
    $env:npm_config_offline = 'true'
    $env:npm_config_update_notifier = 'false'
    foreach ($architecture in @('x64', 'arm64')) {
        foreach ($template in $templates) {
            $id = "$architecture-$($template.id)"
            $caseRoot = Join-Path $TestRoot $id
            $packageSource = Join-Path $caseRoot 'package-source'
            $packageDirectory = Join-Path $caseRoot 'package'
            $payloadDirectory = Join-Path $caseRoot 'payload'
            $runner = Join-Path $caseRoot 'runner'
            New-Item -Path $packageSource, $packageDirectory, $runner -ItemType Directory | Out-Null
            $extension = if ($template.kind -eq 'missing-extensions') {
                'dist\fixture'
            } elseif ($template.kind -eq 'preexisting-plugin') {
                'dist\extensions\gateway-isolation'
            } else {
                'dist\extensions\fixture'
            }
            $extensionPath = Join-Path $packageSource $extension
            New-Item -Path $extensionPath -ItemType Directory -Force | Out-Null
            '{"name":"synthetic-extension-fixture","version":"1.0.0"}' |
                Set-Content -LiteralPath (Join-Path $extensionPath 'package.json') -Encoding utf8
            '{"name":"openclaw","version":"2026.8.2","type":"module"}' |
                Set-Content -LiteralPath (Join-Path $packageSource 'package.json') -Encoding utf8
            $runtimeFixture | Set-Content -LiteralPath (Join-Path $packageSource 'openclaw.mjs') -Encoding utf8
            $inspection = @{
                plugin = @{
                    id = 'gateway-isolation'; origin = 'bundled'; enabled = $true
                    activated = $true; status = 'loaded'; imported = $true; httpRoutes = 1
                }
                httpRouteCount = 1; gatewayMethods = @(); tools = @(); services = @(); diagnostics = @()
            }
            if ($template.field) {
                $parts = $template.field.Split('.')
                $container = if ($parts.Count -eq 2) { $inspection.plugin } else { $inspection }
                $key = $parts[-1]
                if ($template.kind -eq 'missing') { $container.Remove($key) }
                else { $container[$key] = $template.value }
            }
            @{
                inspection = $inspection
                exitCode = $(if ($template.kind -eq 'exit') { 23 } else { 0 })
            } | ConvertTo-Json -Depth 10 |
                Set-Content -LiteralPath (Join-Path $packageSource 'inspection-fixture.json') -Encoding utf8
            @{
                repository = 'synthetic-local-fixture'
                requestedRef = 'synthetic-local-fixture'
                resolvedCommit = '0000000000000000000000000000000000000000'
                packageVersion = '2026.8.2'
            } | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $packageDirectory 'source.json') -Encoding utf8
            Push-Location $packageSource
            try {
                $packOutput = & npm pack --offline --ignore-scripts --pack-destination $packageDirectory --silent 2>&1
                Assert-Matrix ($LASTEXITCODE -eq 0) "npm pack failed for $id."
            }
            finally { Pop-Location }
            $env:RUNNER_TEMP = $runner
            $env:PAYLOAD_MATRIX_OBSERVATIONS = Join-Path $caseRoot 'runtime-observations.jsonl'
            $env:PAYLOAD_MATRIX_EXPECTED_STATE_DIR = Join-Path $runner "openclaw-stage-$architecture\gateway-isolation-validation"
            foreach ($name in $buildEnvironmentNames | Where-Object { $_ -ne 'RUNNER_TEMP' }) {
                if ($template.environment -eq 'absent') {
                    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                } else {
                    $value = if ($template.environment -eq 'empty') { '' } else { "matrix-original-$name" }
                    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
                }
            }
            $before = Get-EnvironmentSnapshot $buildEnvironmentNames
            $beforeLocation = (Get-Location).Path
            $actualReason = 'accepted'
            try {
                & (Join-Path $repositoryRoot 'scripts\Build-Payload.ps1') `
                    -PackageDirectory $packageDirectory `
                    -Architecture $architecture `
                    -OutputDirectory $payloadDirectory *> (Join-Path $caseRoot 'build-private.txt')
            }
            catch {
                $actualReason = ConvertTo-PublicText $_.Exception.Message
            }
            $expectedReason = $template.reason.Replace('<Case>', $id).Replace('<Architecture>', $architecture)
            Assert-Matrix ($actualReason -eq $expectedReason) "Unexpected result for ${id}: $actualReason"
            $restored = [ordered]@{}
            foreach ($name in $buildEnvironmentNames) {
                $restored[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') -ceq $before[$name]
                Assert-Matrix $restored[$name] "$id did not restore $name."
            }
            Assert-Matrix ((Get-Location).Path -eq $beforeLocation) "$id did not restore the working directory."
            $observations = @()
            if (Test-Path -LiteralPath $env:PAYLOAD_MATRIX_OBSERVATIONS) {
                $observations = @(Get-Content -LiteralPath $env:PAYLOAD_MATRIX_OBSERVATIONS | ForEach-Object { $_ | ConvertFrom-Json })
            }
            $expectedCalls = if ($template.kind -in @('missing-extensions', 'preexisting-plugin')) {
                @()
            } elseif ($template.kind -eq 'accepted' -and $architecture -eq 'x64') {
                @('runtime-inspection', 'version-smoke')
            } else {
                @('runtime-inspection')
            }
            Assert-Matrix (($observations.command -join ',') -eq ($expectedCalls -join ',')) "$id invoked unexpected runtime commands."
            foreach ($observation in $observations) {
                Assert-Matrix ($observation.nodeVersion -eq 'v24.16.0') "$id used an unexpected Node runtime."
                if ($observation.command -eq 'runtime-inspection') {
                    Assert-Matrix ($observation.isolationMode -eq 'disabled') "$id did not disable isolation for inspection."
                    Assert-Matrix $observation.stateDirectoryMatchesExpected "$id did not isolate the inspection state directory."
                }
            }
            $content = $null
            if ($template.kind -eq 'accepted') {
                $pluginSource = Join-Path $repositoryRoot 'plugins\gateway-isolation'
                $pluginTarget = Join-Path $payloadDirectory 'app\dist\extensions\gateway-isolation'
                $files = @(Get-ChildItem -LiteralPath $pluginTarget -File -Recurse)
                $names = @($files | ForEach-Object { [IO.Path]::GetRelativePath($pluginTarget, $_.FullName) } | Sort-Object)
                Assert-Matrix (($names -join ',') -eq 'index.js,openclaw.plugin.json,package.json') "$id did not ship exactly the three required files."
                $hashes = foreach ($name in $names) {
                    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $pluginSource $name)).Hash.ToLowerInvariant()
                    $targetHash = (Get-FileHash -LiteralPath (Join-Path $pluginTarget $name)).Hash.ToLowerInvariant()
                    Assert-Matrix ($sourceHash -eq $targetHash) "$id altered plugin file $name."
                    [ordered]@{ file = $name; sourceSha256 = $sourceHash; payloadSha256 = $targetHash; identical = $true }
                }
                $noTestFile = -not (Test-Path -LiteralPath (Join-Path $pluginTarget 'index.test.js'))
                Assert-Matrix $noTestFile "$id shipped plugin test code."
                $metadata = Get-Content -LiteralPath (Join-Path $payloadDirectory 'payload-metadata.json') -Raw | ConvertFrom-Json
                Assert-Matrix ($metadata.architecture -eq $architecture) "$id wrote incorrect architecture metadata."
                Assert-Matrix ($metadata.layout -eq 'expanded-directory') "$id wrote incorrect layout metadata."
                Assert-Matrix ($metadata.nodeVersion -eq $nodeVersion -and $metadata.npmVersion -eq $npmVersion) "$id wrote incorrect runtime metadata."
                $content = [ordered]@{
                    shippingFileCount = $files.Count; files = @($hashes); noTestFile = $noTestFile
                    metadataArchitecture = $metadata.architecture; metadataLayout = $metadata.layout
                    metadataNodeVersion = $metadata.nodeVersion; metadataNpmVersion = $metadata.npmVersion
                }
            } else {
                Assert-Matrix (-not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'app'))) "$id published a rejected application."
                Assert-Matrix (-not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'payload-metadata.json'))) "$id published rejected metadata."
            }
            $command = "& .\scripts\Build-Payload.ps1 -PackageDirectory <TestRoot>\$id\package -Architecture $architecture -OutputDirectory <TestRoot>\$id\payload"
            $result = [ordered]@{
                id = $id; architecture = $architecture; outcome = 'PASS'
                fixtureType = 'synthetic-local-npm-package-and-runtime-inspection'
                mutation = $template.kind; field = $template.field
                fixtureInspection = $inspection
                expected = $expectedReason; actual = $actualReason
                command = $command
                environmentBefore = $(if ($template.environment -in @('absent', 'empty')) { $template.environment } else { 'existing sentinels' })
                environmentRestored = $restored; workingDirectoryRestored = $true
                runtimeObservations = $observations
                content = $content
                rejectedOutputAbsent = $template.kind -ne 'accepted'
            }
            $results.Add($result)
            $transcript.Add('')
            $transcript.Add("CASE $id PASS")
            $transcript.Add("Command (from <TestRoot>\$id\package-source): npm pack --offline --ignore-scripts --pack-destination <TestRoot>\$id\package --silent")
            $transcript.Add("Command: $command")
            $transcript.Add("Expected: $expectedReason")
            $transcript.Add("Actual: $actualReason")
            $transcript.Add("Environment restored: $($buildEnvironmentNames -join ', '); working directory restored: true")
            $transcript.Add("Runtime calls: $($expectedCalls -join ', ')")
            if ($content) {
                $transcript.Add("Shipping plugin files: 3; all source/payload SHA-256 hashes identical; index.test.js absent; metadata architecture: $architecture")
                foreach ($hash in $content.files) { $transcript.Add("SHA256 $($hash.file) $($hash.payloadSha256)") }
            } else {
                $transcript.Add('Rejected application and payload metadata absent.')
            }
            Write-Host "PASS $id"
        }
    }
}
finally {
    Restore-EnvironmentSnapshot $originalEnvironment
    if ($createdRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force
    }
}
foreach ($name in $environmentNames) {
    Assert-Matrix ([Environment]::GetEnvironmentVariable($name, 'Process') -ceq $originalEnvironment[$name]) "Harness did not restore $name."
}
Assert-Matrix ((Get-Location).Path -eq $originalLocation) 'Harness did not restore the working directory.'
Assert-Matrix (-not (Test-Path -LiteralPath $TestRoot)) 'Generated fixture directory was not cleaned up.'
$acceptedCount = @($results | Where-Object { $_.actual -eq 'accepted' }).Count
$transcript.Add('')
$transcript.Add("RESULT: $($results.Count) passed; $acceptedCount accepted; $($results.Count - $acceptedCount) rejected as expected; 0 failed.")
$transcript.Add('Harness environment and working directory restored. Generated TestRoot removed. No preexisting directories were removed.')
$evidence = [ordered]@{
    schemaVersion = 1
    fixtureType = 'synthetic-local-npm-packages-and-synthetic-runtime-inspection-responses'
    scope = 'Actual Build-Payload.ps1 x64 and ARM64 composition contract with controlled runtime-inspection fixtures.'
    productionSourceCommit = $ProductionSourceCommit
    evidenceCheckoutHead = $head
    verifiedSourceFiles = @($sourceHashes)
    harness = 'scripts/validation/Test-GatewayIsolationPayloadMatrix.ps1'
    harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    environment = [ordered]@{ powershell = $PSVersionTable.PSVersion.ToString(); node = $nodeVersion; npm = $npmVersion; host = 'Windows x64'; npmOffline = $true }
    command = 'pwsh -NoProfile -File .\scripts\validation\Test-GatewayIsolationPayloadMatrix.ps1'
    summary = [ordered]@{ total = $results.Count; passed = $results.Count; failed = 0; accepted = $acceptedCount; rejected = $results.Count - $acceptedCount; architectures = @('x64', 'arm64') }
    harnessEnvironmentRestored = $true
    harnessWorkingDirectoryRestored = $true
    generatedFixtureDirectoryRemoved = $true
    cases = @($results.ToArray())
}
$json = $evidence | ConvertTo-Json -Depth 20
$text = $transcript -join [Environment]::NewLine
foreach ($publicOutput in @($json, $text)) {
    Assert-Matrix ($publicOutput -notmatch '(?i)[a-z]:[\\/]') 'Evidence contains an absolute local path.'
    Assert-Matrix (-not $publicOutput.Contains($env:USERNAME)) 'Evidence contains the local username.'
}
New-Item -Path $EvidenceDirectory -ItemType Directory -Force | Out-Null
$json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'payload-matrix.json') -Encoding utf8
$text | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'payload-matrix.txt') -Encoding utf8
Write-Host "Payload matrix passed: $($results.Count) cases. Sanitized JSON and transcript written."
