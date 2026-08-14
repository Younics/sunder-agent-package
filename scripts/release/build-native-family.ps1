param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
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
$packages = @($inventory.packages | Where-Object { $null -ne $_.workerProjectPath } | Sort-Object key)
if ($packages.Count -ne 15) {
    throw "Expected exactly 15 worker leaves, found $($packages.Count)."
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$archiveDirectory = Join-Path $OutputDirectory "current-rid-archives"
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
$signingTargets = Join-Path $repositoryRoot "scripts/release/Sunder.Agent.ReleaseSigning.targets"
$signingScript = Join-Path $repositoryRoot "scripts/release/sign-worker-apphost.ps1"
$requiresSigning = $Rid.StartsWith("win-", [StringComparison]::Ordinal) -or $Rid.StartsWith("osx-", [StringComparison]::Ordinal)

function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Resolve-ProjectPath([string] $Path) {
    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
}

$commonProperties = @(
    "-p:SunderUseSourceSdk=false",
    "-p:SunderAgentVersion=$Version",
    "-p:ContinuousIntegrationBuild=true",
    "-p:RestoreLockedMode=true"
)

foreach ($package in $packages) {
    $workerProject = Resolve-ProjectPath $package.workerProjectPath
    Invoke-DotNet @("restore", $workerProject, "--locked-mode", "-r", $Rid, "--source", $env:NUGET_SOURCE, "-p:SunderUseSourceSdk=false", "-p:RestoreLockedMode=true")

    $buildArguments = @("build", $workerProject, "-c", "Release", "-r", $Rid, "--no-restore", "-m:1") + $commonProperties
    if ($requiresSigning) {
        $buildArguments += @(
            "-p:CustomAfterMicrosoftCommonTargets=$signingTargets",
            "-p:SunderPackageWorkerSigningTargets=SignSunderAgentReleaseWorkerAppHost",
            "-p:SunderAgentReleaseSigningScript=$signingScript"
        )
    }
    Invoke-DotNet $buildArguments

    $workerDirectory = Split-Path -Parent $workerProject
    $leafPath = Join-Path $workerDirectory "bin/Release/net10.0/$Rid/sunder-dev"
    $manifestPath = Join-Path $leafPath "manifest/sunder-package.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Worker leaf was not emitted for '$($package.packageId)': $leafPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $runtimeTargets = @($manifest.targets | Where-Object { $_.role -ceq "runtime" })
    if ($runtimeTargets.Count -ne 1 -or $runtimeTargets[0].rid -cne $Rid -or $runtimeTargets[0].kind -cne "worker") {
        throw "Worker leaf '$($package.packageId)' does not contain exactly the native '$Rid' worker target."
    }
    $appHostPath = Join-Path $leafPath ("payload/runtime/$Rid/" + $runtimeTargets[0].entryPoint)
    if (-not (Test-Path -LiteralPath $appHostPath -PathType Leaf)) {
        throw "Official worker apphost was not emitted: $appHostPath"
    }
    if ($requiresSigning) {
        & pwsh -NoLogo -NoProfile -NonInteractive -File $signingScript -Path $appHostPath -Rid $Rid -Mode Verify
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $aggregateProject = Resolve-ProjectPath $package.projectPath
    [xml] $aggregateXml = Get-Content -LiteralPath $aggregateProject -Raw
    $aggregateDirectory = Split-Path -Parent $aggregateProject
    $appProjects = @($aggregateXml.Project.ItemGroup.ProjectReference |
        ForEach-Object { $_.Include } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $aggregateDirectory $_)) })
    foreach ($appProject in $appProjects) {
        Invoke-DotNet @("restore", $appProject, "--locked-mode", "--source", $env:NUGET_SOURCE, "-p:SunderUseSourceSdk=false", "-p:RestoreLockedMode=true")
        Invoke-DotNet (@("build", $appProject, "-c", "Release", "--no-restore", "-m:1") + $commonProperties)
    }

    Invoke-DotNet @("restore", $aggregateProject, "--locked-mode", "--source", $env:NUGET_SOURCE, "-p:SunderUseSourceSdk=false", "-p:RestoreLockedMode=true")
    $archivePath = Join-Path $archiveDirectory "$($package.packageId).$Version.sunderpkg"
    Invoke-DotNet @(
        "msbuild", $aggregateProject,
        "-t:PackSunderPackage", "-m:1",
        "-p:Configuration=Release",
        "-p:BuildProjectReferences=false",
        "-p:SunderAgentUniversalWorkerPackage=false",
        "-p:SunderUseSourceSdk=false",
        "-p:SunderAgentVersion=$Version",
        "-p:ContinuousIntegrationBuild=true",
        "-p:RestoreLockedMode=true",
        "-p:SunderPackageOutputPath=$archivePath"
    )
}

$archives = @(Get-ChildItem -LiteralPath $archiveDirectory -Filter "*.sunderpkg" -File)
if ($archives.Count -ne 15) {
    throw "Expected exactly 15 current-RID archives, found $($archives.Count)."
}
Write-Host "Built exactly 15 native $Rid worker leaves and current-RID smoke archives."
