param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64")]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Sign", "Verify")]
    [string] $Mode
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$Path = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Worker apphost was not found: $Path"
}

function Require-EnvironmentValue([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Release signing value '$Name' is not configured."
    }
    return $value
}

function Find-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "[\\/]x64[\\/]signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "signtool.exe was not found on the Windows release runner."
    }
    return $candidate.FullName
}

if ($Rid.StartsWith("win-", [StringComparison]::Ordinal)) {
    if (-not $IsWindows) {
        throw "RID '$Rid' must be signed and verified on Windows."
    }

    $publisherSubject = Require-EnvironmentValue "SUNDER_WINDOWS_PUBLISHER_SUBJECT"
    $signTool = Find-SignTool
    if ($Mode -eq "Sign") {
        $dlibPath = Require-EnvironmentValue "SUNDER_WINDOWS_SIGN_DLIB"
        $metadataPath = Require-EnvironmentValue "SUNDER_WINDOWS_SIGN_METADATA"
        & $signTool sign /fd SHA256 /td SHA256 /tr http://timestamp.acs.microsoft.com /dlib $dlibPath /dmdf $metadataPath $Path
    }

    & $signTool verify /pa /all /v $Path
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Worker Authenticode signature is not valid: $($signature.Status)."
    }
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -cne $publisherSubject) {
        throw "Worker Authenticode publisher '$($signature.SignerCertificate.Subject)' does not match '$publisherSubject'."
    }
    return
}

if (-not $IsMacOS) {
    throw "RID '$Rid' must be signed and verified on macOS."
}

$identity = Require-EnvironmentValue "SUNDER_APPLE_SIGN_IDENTITY"
$teamId = Require-EnvironmentValue "SUNDER_APPLE_TEAM_ID"
$keychainPath = Require-EnvironmentValue "SUNDER_APPLE_SIGNING_KEYCHAIN"
if ($Mode -eq "Sign") {
    & /usr/bin/codesign --force --options runtime --timestamp --sign $identity --keychain $keychainPath $Path
}

& /usr/bin/codesign --verify --strict --verbose=2 $Path
$details = (& /usr/bin/codesign --display --verbose=4 $Path 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "codesign could not read the worker signature."
}
$teamMatch = [regex]::Match($details, "(?m)^TeamIdentifier=(?<team>[^\r\n]+)$")
if (-not $teamMatch.Success -or $teamMatch.Groups["team"].Value -cne $teamId) {
    throw "Worker Apple TeamIdentifier does not match '$teamId'."
}
if ($details -notmatch "(?m)^Authority=$([regex]::Escape($identity))$") {
    throw "Worker Apple signing authority does not match '$identity'."
}
