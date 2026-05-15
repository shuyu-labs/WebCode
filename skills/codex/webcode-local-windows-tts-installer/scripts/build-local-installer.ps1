[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Configuration = "Debug",
    [string]$Version,
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputRoot,
    [string]$ReplyTtsSourceRoot,
    [string]$ReplyTtsFfmpegExecutablePath
)

$ErrorActionPreference = "Stop"

function Test-IsWebCodeRepoRoot {
    param([string]$CandidateRoot)

    if ([string]::IsNullOrWhiteSpace($CandidateRoot)) {
        return $false
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($CandidateRoot)
    return (
        (Test-Path (Join-Path $resolvedRoot "Directory.Build.props")) -and
        (Test-Path (Join-Path $resolvedRoot "tools\build-windows-installer.ps1")) -and
        (Test-Path (Join-Path $resolvedRoot "installer\windows\WebCode.iss"))
    )
}

function Resolve-WebCodeRepoRoot {
    param([string]$RequestedRepoRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedRepoRoot)) {
        if (-not (Test-IsWebCodeRepoRoot -CandidateRoot $RequestedRepoRoot)) {
            throw "Repo root '$RequestedRepoRoot' does not look like a WebCode checkout."
        }

        return [System.IO.Path]::GetFullPath($RequestedRepoRoot)
    }

    $directory = Get-Location
    while ($directory -ne $null) {
        if (Test-IsWebCodeRepoRoot -CandidateRoot $directory.Path) {
            return $directory.Path
        }

        $directory = $directory.Parent
    }

    throw "Could not locate the WebCode repo root from the current working directory. Pass -RepoRoot explicitly."
}

function Get-BuildVersion {
    param(
        [string]$RequestedVersion,
        [string]$ResolvedRepoRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
        return $RequestedVersion.TrimStart("v")
    }

    [xml]$props = Get-Content -Path (Join-Path $ResolvedRepoRoot "Directory.Build.props")
    $resolvedVersion = $props.Project.PropertyGroup.Version
    if (-not $resolvedVersion) {
        throw "Could not resolve version from Directory.Build.props."
    }

    return $resolvedVersion.TrimStart("v")
}

$resolvedRepoRoot = Resolve-WebCodeRepoRoot -RequestedRepoRoot $RepoRoot
$resolvedVersion = Get-BuildVersion -RequestedVersion $Version -ResolvedRepoRoot $resolvedRepoRoot
$buildScriptPath = Join-Path $resolvedRepoRoot "tools\build-windows-installer.ps1"

$buildParams = @{
    Configuration     = $Configuration
    RuntimeIdentifier = $RuntimeIdentifier
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $buildParams.Version = $Version
}

if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $buildParams.OutputRoot = $OutputRoot
}

if (-not [string]::IsNullOrWhiteSpace($ReplyTtsSourceRoot)) {
    $buildParams.ReplyTtsSourceRoot = $ReplyTtsSourceRoot
}

if (-not [string]::IsNullOrWhiteSpace($ReplyTtsFfmpegExecutablePath)) {
    $buildParams.ReplyTtsFfmpegExecutablePath = $ReplyTtsFfmpegExecutablePath
}

Push-Location $resolvedRepoRoot
try {
    & $buildScriptPath @buildParams
    if ($LASTEXITCODE -ne 0) {
        throw "Local Windows installer build failed."
    }
}
finally {
    Pop-Location
}

$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $resolvedRepoRoot "artifacts\windows-installer"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $resolvedRepoRoot $OutputRoot))
}

$versionTag = "v$resolvedVersion"
$releaseRoot = Join-Path $resolvedOutputRoot $versionTag
$installerPath = Join-Path $releaseRoot "installer\WebCode-Setup-$versionTag-$RuntimeIdentifier.exe"
$portableZipPath = Join-Path $releaseRoot "WebCode-$versionTag-$RuntimeIdentifier-portable.zip"
$checksumsPath = Join-Path $releaseRoot "SHA256SUMS.txt"
$releaseNotesPath = Join-Path $releaseRoot "RELEASE_NOTES.md"

Write-Host ""
Write-Host "Local Windows installer artifacts"
Write-Host "RepoRoot: $resolvedRepoRoot"
Write-Host "Installer: $installerPath"
Write-Host "Portable ZIP: $portableZipPath"
Write-Host "Checksums: $checksumsPath"
Write-Host "Release notes: $releaseNotesPath"
