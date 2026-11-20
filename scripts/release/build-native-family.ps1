param(
    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $InputsDirectory,
    [string] $CatalogPath,
    [string] $ToolsDirectory,
    [string] $Package
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$repositoryRoot = [System.IO.Path]::GetFullPath((Get-Location).Path)
$inventoryPath = Join-Path $repositoryRoot "packages.json"
if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf)) {
    throw "Run build-native-family.ps1 from the sunder-agent-package repository root."
}

$nativeRid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
if ($nativeRid -cne $Rid) {
    throw "Native release RID '$Rid' cannot run on '$nativeRid'. Configure the RID runner mapping to a matching native runner."
}

$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$policy = & python3 -B (Join-Path $PSScriptRoot "family_policy.py") | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $Rid -cnotin $policy.rids) { throw "Invalid native family policy/platform." }
$familyCount = @($policy.packageIds).Count
$packages = @($inventory.packages | Where-Object { $_.artifactType -ceq "sunderpkg" } | Sort-Object key)
if ($packages.Count -ne $familyCount -or @($inventory.packages).Count -ne $familyCount -or
    @($packages.packageId | Sort-Object -Unique).Count -ne $familyCount) {
    throw "Expected the complete reviewed family of $familyCount unique canonical package archives."
}
$selective = $Version.EndsWith(".json", [StringComparison]::Ordinal) -and (Test-Path -LiteralPath $Version -PathType Leaf)
if ($selective) {
    $Version = [System.IO.Path]::GetFullPath($Version)
    $selectedJson = & python3 -B (Join-Path $PSScriptRoot "agent_selection.py") rows --source $repositoryRoot --plan $Version
    if ($LASTEXITCODE -ne 0) { throw "Invalid package release selection." }
    $packages = @($selectedJson | ConvertFrom-Json)
}
if ($Package) {
    $packages = @($packages | Where-Object { $_.packageId -ceq $Package })
    if ($packages.Count -ne 1) { throw "Native component is outside the selected package scope: $Package" }
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$archiveDirectory = Join-Path $OutputDirectory "current-rid-archives"
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null

function Invoke-DotNet([string[]] $Arguments) {
    & python3 -B $ciHelper run --state $ciState -- dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Resolve-ProjectPath([string] $Path) {
    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
}

$commonProperties = @(
    "-p:SunderUseSourceSdk=$($env:SUNDER_NATIVE_USE_SOURCE_SDK -ceq 'true' ? 'true' : 'false')",
    "-p:Configuration=Release",
    "-p:ContinuousIntegrationBuild=true",
    "-p:SunderPackageRoleInputSourceRoot=$repositoryRoot"
)
if ($env:SUNDER_NATIVE_USE_SOURCE_SDK -cne 'true') {
    $commonProperties += "-p:RestoreLockedMode=true"
}
if ($env:SUNDER_NATIVE_TASK_ASSEMBLY) {
    $commonProperties += @(
        "-p:SunderPackageBuildTaskAssembly=$([System.IO.Path]::GetFullPath($env:SUNDER_NATIVE_TASK_ASSEMBLY))",
        "-p:SunderPackageBuildTaskSourceIsImmutable=true"
    )
}

# Public baselines must already be frozen before the first Build. Private-only
# baseline absence and immutable retired private records are also qualified inputs;
# changing them between families invalidates earlier qualifications of the tree.
$nativeHelper = Join-Path $PSScriptRoot "native-family.py"
$releaseInputHelper = Join-Path $PSScriptRoot "release_inputs.py"
$ciHelper = Join-Path $PSScriptRoot "../ci/agent_ci.py"
& python3 -B $releaseInputHelper check-python
if ($LASTEXITCODE -ne 0) { throw "Python 3.9 or newer is required." }
$preflightArguments = @("producer-preflight", $repositoryRoot, $Version)
if ($Package) { $preflightArguments += $Package }
& python3 -B $nativeHelper @preflightArguments
if ($LASTEXITCODE -ne 0) { throw "Native producer prerequisites failed." }

$coldWork = Join-Path ([System.IO.Path]::GetTempPath()) ("sunder-agent-cold-" + $Rid + "-" + [guid]::NewGuid().ToString("N"))
$mode = $env:SUNDER_NATIVE_USE_SOURCE_SDK -ceq 'true' ? 'source' : 'package'
$prepareArguments = @("prepare", "--source", $repositoryRoot, "--core", (Join-Path $repositoryRoot "../sunder-core"),
    "--work", $coldWork, "--mode", $mode, "--packages", (ConvertTo-Json -InputObject @($packages.packageId) -Compress))
if ($InputsDirectory) { $prepareArguments += @("--inputs", $InputsDirectory) }
elseif ($CatalogPath) { $prepareArguments += @("--catalog", $CatalogPath) }
else { throw "Supply the verified dependency handoff or the import-free owner's exact catalog." }
if ($ToolsDirectory) { $prepareArguments += @("--tools", $ToolsDirectory) }
& python3 -B $ciHelper @prepareArguments
if ($LASTEXITCODE -ne 0) { throw "Declared dependency preparation failed." }
$ciState = Join-Path $coldWork "ci-state.json"

foreach ($entry in $packages) {
    $packageVersion = $selective ? $entry.version : $Version
    $project = Resolve-ProjectPath $entry.projectPath
    # Shared preparation restored the selected project graph with the same Release/CI
    # properties and hydrated exact locks. Build/prebuilt Pack use that prepared
    # state; denied-network diagnostics belong to explicit cold acceptance only.
    Invoke-DotNet (@("build", $project, "-c", "Release", "-r", $Rid, "--no-restore") + $commonProperties)

    # The canonical build emits generated Worker/App roles and one combined RID leaf.
    $nativeRoot = & python3 -B $nativeHelper native-root $repositoryRoot $entry.packageId $Rid
    if ($LASTEXITCODE -ne 0) { throw "Missing/ambiguous native output for '$($entry.packageId)'." }
    $leafPath = Join-Path $nativeRoot "sunder-dev"
    $manifestPath = Join-Path $leafPath "manifest/sunder-package.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Worker leaf was not emitted for '$($entry.packageId)': $leafPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $runtimeTargets = @($manifest.targets | Where-Object { $_.role -ceq "runtime" })
    $expectedEntryPoint = "lib/$([System.IO.Path]::GetFileNameWithoutExtension($project)).dll"
    if ($runtimeTargets.Count -ne 1 -or
        $runtimeTargets[0].rid -cne $Rid -or
        $runtimeTargets[0].kind -cne "worker" -or
        $runtimeTargets[0].entryPoint -cne $expectedEntryPoint) {
        throw "Worker leaf '$($entry.packageId)' does not contain exactly the native '$Rid' managed module target."
    }
    $modulePath = Join-Path $leafPath ("payload/runtime/$Rid/" + $runtimeTargets[0].entryPoint)
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
        throw "Official framework-dependent Worker module was not emitted: $modulePath"
    }
    # The selection helper resolves .runtimeconfig.json/.deps.json and rejects
    # obsolete apphosts across Core's shared/role-shared/exact-RID projection.

    $archivePath = Join-Path $archiveDirectory "$($entry.packageId).$packageVersion.sunderpkg"
    # Retain the whole Core bundle. This command writes only a compact selection;
    # GetSunderPackageRoleInputs is deliberately avoided because it depends on Build.
    $selectionPath = & python3 -B $nativeHelper select-native $repositoryRoot $entry.packageId $Rid $packageVersion
    if ($LASTEXITCODE -ne 0) { throw "Native qualification selection failed for '$($entry.packageId)'." }
    # Component leaves are composed exactly once after all six native jobs finish.
    # The final candidate is then smoked on every platform without repacking.
    if ($Package) { continue }
    Invoke-DotNet (@(
        "msbuild", $project,
        "-t:PackSunderPackage",
        "-p:Configuration=Release",
        "-p:BuildProjectReferences=false",
        "-p:SunderPackageUsePrebuiltLeaves=true",
        "-p:SunderPackageComposeRuntimeIdentifiers=$Rid",
        "-p:SunderPackagePrebuiltRoleInputsFile=$selectionPath",
        "-p:SunderPackageOutputPath=$archivePath"
    ) + $commonProperties)
}

if ($Package) {
    Write-Host "Qualified native $Rid role bytes for $Package. Ready for immutable component collection."
    exit 0
}

if ($selective) {
    $plan = Get-Content -LiteralPath $Version -Raw | ConvertFrom-Json
    foreach ($entry in @($plan.packages | Where-Object { -not $_.selected })) {
        $retained = Join-Path $env:SUNDER_AGENT_RETAINED_ARCHIVE_DIR $entry.retainedArchive.name
        if ((Get-FileHash -LiteralPath $retained -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.retainedArchive.sha256) {
            throw "Retained archive changed for '$($entry.packageId)'."
        }
        Copy-Item -LiteralPath $retained -Destination (Join-Path $archiveDirectory $entry.retainedArchive.name)
    }
}

$archives = @(Get-ChildItem -LiteralPath $archiveDirectory -Filter "*.sunderpkg" -File)
if ($archives.Count -ne $familyCount) {
    throw "Expected $familyCount current-RID family archives, found $($archives.Count)."
}
Write-Host "Built $($packages.Count) selected native $Rid leaves; all $familyCount family archives are required for native smoke tests."
