param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ColdInputsDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ColdManifestSha256,

    [Parameter(Mandatory = $true)]
    [string] $ArtifactSourceCommit,

    [Parameter(Mandatory = $true)]
    [string] $CoreCommit
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
$packages = @($inventory.packages | Where-Object { $_.artifactType -ceq "sunderpkg" } | Sort-Object key)
if ($packages.Count -ne 15 -or @($inventory.packages).Count -ne 15 -or
    @($packages.packageId | Sort-Object -Unique).Count -ne 15) {
    throw "Expected exactly 15 unique canonical package archives."
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$archiveDirectory = Join-Path $OutputDirectory "current-rid-archives"
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null

function Invoke-DotNet([string[]] $Arguments) {
    & python3 -B $releaseInputHelper exec $coldState -- dotnet @Arguments
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
    "-p:SunderAgentVersion=$Version",
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
& python3 -B $releaseInputHelper check-python
if ($LASTEXITCODE -ne 0) { throw "Python 3.9 or newer is required." }
& python3 -B $nativeHelper producer-preflight $repositoryRoot $Version
if ($LASTEXITCODE -ne 0) { throw "Native producer prerequisites failed." }

$coldWork = Join-Path ([System.IO.Path]::GetTempPath()) ("sunder-agent-cold-" + $Rid + "-" + [guid]::NewGuid().ToString("N"))
$mode = $env:SUNDER_NATIVE_USE_SOURCE_SDK -ceq 'true' ? 'source' : 'package'
& python3 -B $releaseInputHelper prepare $repositoryRoot $ColdInputsDirectory $coldWork $mode $ArtifactSourceCommit $CoreCommit $ColdManifestSha256
if ($LASTEXITCODE -ne 0) { throw "Reviewed cold input delivery/hydration failed; no build fallback is allowed." }
$coldState = Join-Path $coldWork "cold-state.json"

foreach ($package in $packages) {
    $project = Resolve-ProjectPath $package.projectPath
    # Shared preparation restored the full six-RID graph with these same Release,
    # version and CI properties, proved the cold diagnostic, and hydrated exact locks.
    # Build and prebuilt Pack now run in the checked network-denied process tree.
    Invoke-DotNet (@("build", $project, "-c", "Release", "-r", $Rid, "--no-restore", "-m:1") + $commonProperties)

    $projectDirectory = Split-Path -Parent $project
    # The canonical build emits generated Worker/App roles and one combined RID leaf.
    $leafPath = Join-Path $projectDirectory "bin/Release/net10.0/sunder-targets/$Rid/sunder-dev"
    $manifestPath = Join-Path $leafPath "manifest/sunder-package.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Worker leaf was not emitted for '$($package.packageId)': $leafPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $runtimeTargets = @($manifest.targets | Where-Object { $_.role -ceq "runtime" })
    $expectedEntryPoint = "lib/$([System.IO.Path]::GetFileNameWithoutExtension($project)).dll"
    if ($runtimeTargets.Count -ne 1 -or
        $runtimeTargets[0].rid -cne $Rid -or
        $runtimeTargets[0].kind -cne "worker" -or
        $runtimeTargets[0].entryPoint -cne $expectedEntryPoint) {
        throw "Worker leaf '$($package.packageId)' does not contain exactly the native '$Rid' managed module target."
    }
    $modulePath = Join-Path $leafPath ("payload/runtime/$Rid/" + $runtimeTargets[0].entryPoint)
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
        throw "Official framework-dependent Worker module was not emitted: $modulePath"
    }
    # The selection helper resolves .runtimeconfig.json/.deps.json and rejects
    # obsolete apphosts across Core's shared/role-shared/exact-RID projection.

    $archivePath = Join-Path $archiveDirectory "$($package.packageId).$Version.sunderpkg"
    # Retain the whole Core bundle. This command writes only a compact selection;
    # GetSunderPackageRoleInputs is deliberately avoided because it depends on Build.
    $selectionPath = & python3 -B $nativeHelper select-native $repositoryRoot $package.packageId $Rid $Version
    if ($LASTEXITCODE -ne 0) { throw "Native qualification selection failed for '$($package.packageId)'." }
    Invoke-DotNet (@(
        "msbuild", $project,
        "-t:PackSunderPackage", "-m:1",
        "-p:Configuration=Release",
        "-p:BuildProjectReferences=false",
        "-p:SunderPackageUsePrebuiltLeaves=true",
        "-p:SunderPackageComposeRuntimeIdentifiers=$Rid",
        "-p:SunderPackagePrebuiltRoleInputsFile=$selectionPath",
        "-p:SunderPackageOutputPath=$archivePath"
    ) + $commonProperties)
}

$archives = @(Get-ChildItem -LiteralPath $archiveDirectory -Filter "*.sunderpkg" -File)
if ($archives.Count -ne 15) {
    throw "Expected exactly 15 current-RID archives, found $($archives.Count)."
}
Write-Host "Built exactly 15 native $Rid framework-dependent Worker leaves and composed current-RID smoke archives without rebuilding them."
