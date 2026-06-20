[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$ProjectPath,
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Web.Extensions

if (-not $ProjectPath) {
    $ProjectPath = Join-Path $PSScriptRoot "..\WebCodeCli\WebCodeCli.csproj"
}

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $PSScriptRoot "..\artifacts\windows-installer"
}

function Get-RepoRoot {
    param([string]$ScriptRoot)

    return (Resolve-Path (Join-Path $ScriptRoot "..")).Path
}

function Get-BuildVersion {
    param(
        [string]$RequestedVersion,
        [string]$RepoRoot
    )

    if ($RequestedVersion) {
        return $RequestedVersion.TrimStart("v")
    }

    [xml]$props = Get-Content -Path (Join-Path $RepoRoot "Directory.Build.props")
    $resolvedVersion = $props.Project.PropertyGroup.Version
    if (-not $resolvedVersion) {
        throw "Could not resolve version from Directory.Build.props."
    }

    return $resolvedVersion.TrimStart("v")
}

function Read-JsonObject {
    param([string]$Path)

    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
    return $serializer.DeserializeObject($raw)
}

function Write-JsonObject {
    param(
        [string]$Path,
        [object]$Value
    )

    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $json = $serializer.Serialize($Value)
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Update-PublishAppSettings {
    param([string]$PublishDirectory)

    $settingsPath = Join-Path $PublishDirectory "appsettings.json"
    if (-not (Test-Path $settingsPath)) {
        throw "Published appsettings.json was not found at $settingsPath"
    }

    $settings = Read-JsonObject -Path $settingsPath

    if ($settings.ContainsKey("Logging")) {
        $logging = $settings["Logging"]
        if ($logging -and $logging.ContainsKey("LogLevel")) {
            $logLevel = $logging["LogLevel"]
            if ($logLevel) {
                $logLevel["Default"] = "Information"
            }
        }
    }

    $dbConnection = $null
    if ($settings.ContainsKey("DBConnection")) {
        $dbConnection = $settings["DBConnection"]
    }
    else {
        $dbConnection = @{}
        $settings["DBConnection"] = $dbConnection
    }

    if ($dbConnection) {
        $dbConnection["DbType"] = "Sqlite"
        $dbConnection["ConnectionStrings"] = "Data Source=data/WebCodeCli.db"
        $dbConnection["VectorConnection"] = "data/WebCodeCliMem.db"
    }

    if ($settings.ContainsKey("CliTools")) {
        $cliTools = $settings["CliTools"]
        if ($cliTools) {
            $cliTools["TempWorkspaceRoot"] = "workspaces"
        }
    }

    if ($settings.ContainsKey("Workspace")) {
        $workspace = $settings["Workspace"]
        if ($workspace) {
            $workspace["AllowedRoots"] = @("workspaces")
            $workspace["AutoCreateMissingDirectories"] = $true
        }
    }

    Write-JsonObject -Path $settingsPath -Value $settings
}

function Get-LaunchUrlForReleaseNotes {
    param([string]$PublishDirectory)

    $settingsPath = Join-Path $PublishDirectory "appsettings.json"
    if (-not (Test-Path $settingsPath)) {
        throw "Published appsettings.json was not found at $settingsPath"
    }

    $settings = Read-JsonObject -Path $settingsPath
    $configuredUrls = if ($settings.ContainsKey("urls")) { $settings["urls"] } else { $null }
    if (-not $configuredUrls) {
        return "http://localhost:5000"
    }

    $firstUrl = ($configuredUrls -split ';' | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1).Trim()
    if (-not $firstUrl) {
        return "http://localhost:5000"
    }

    return ($firstUrl -replace '://(\*|\+|0\.0\.0\.0)', '://localhost')
}

function Get-InnoSetupCompilerPath {
    $candidatePaths = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ }

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path $candidatePath) {
            return $candidatePath
        }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 before building the installer."
}

$repoRoot = Get-RepoRoot -ScriptRoot $PSScriptRoot
$projectFullPath = (Resolve-Path $ProjectPath).Path
$resolvedVersion = Get-BuildVersion -RequestedVersion $Version -RepoRoot $repoRoot
$assemblyVersion = "$resolvedVersion.0"

if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Resolved version '$resolvedVersion' is not in major.minor.patch format."
}

$versionTag = "v$resolvedVersion"
if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $outputRootFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}

$releaseRoot = Join-Path $outputRootFullPath $versionTag
$publishDirectory = Join-Path $releaseRoot "publish"
$portableDirectoryName = "WebCode-$versionTag-$RuntimeIdentifier-portable"
$portableStageDirectory = Join-Path $releaseRoot $portableDirectoryName
$portableZipPath = Join-Path $releaseRoot "$portableDirectoryName.zip"
$installerOutputDirectory = Join-Path $releaseRoot "installer"
$installerBaseFileName = "WebCode-Setup-$versionTag-$RuntimeIdentifier"
$installerPath = Join-Path $installerOutputDirectory "$installerBaseFileName.exe"
$checksumsPath = Join-Path $releaseRoot "SHA256SUMS.txt"
$releaseNotesPath = Join-Path $releaseRoot "RELEASE_NOTES.md"

if (Test-Path $releaseRoot) {
    Remove-Item -Recurse -Force $releaseRoot
}

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $installerOutputDirectory | Out-Null

Write-Host "Publishing WebCode $resolvedVersion for $RuntimeIdentifier ..."
dotnet publish $projectFullPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    -m:1 `
    --self-contained true `
    -o $publishDirectory `
    /nr:false `
    /p:UseSharedCompilation=false `
    /p:BuildInParallel=false `
    /p:PublishSingleFile=false `
    /p:RunAnalyzers=false `
    /p:GenerateDocumentationFile=false `
    /p:DebugSymbols=false `
    /p:DebugType=None `
    /p:Version=$resolvedVersion `
    /p:AssemblyVersion=$assemblyVersion `
    /p:FileVersion=$assemblyVersion `
    /p:InformationalVersion=$resolvedVersion

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$publishedExePath = Join-Path $publishDirectory "WebCodeCli.exe"
if (-not (Test-Path $publishedExePath)) {
    throw "Expected published executable was not found at $publishedExePath"
}

Update-PublishAppSettings -PublishDirectory $publishDirectory
$launchUrl = Get-LaunchUrlForReleaseNotes -PublishDirectory $publishDirectory

if (Test-Path $portableStageDirectory) {
    Remove-Item -Recurse -Force $portableStageDirectory
}
New-Item -ItemType Directory -Force -Path $portableStageDirectory | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $portableStageDirectory -Recurse -Force

if (Test-Path $portableZipPath) {
    Remove-Item -Force $portableZipPath
}
Compress-Archive -Path $portableStageDirectory -DestinationPath $portableZipPath -Force

$isccPath = Get-InnoSetupCompilerPath
$installerScriptPath = Join-Path $repoRoot "installer\windows\WebCode.iss"

Write-Host "Compiling Windows installer with Inno Setup ..."
& $isccPath `
    "/DMyAppVersion=$resolvedVersion" `
    "/DPublishDir=$publishDirectory" `
    "/DOutputDir=$installerOutputDirectory" `
    "/DMyAppInstallerFileName=$installerBaseFileName" `
    "/DMyAppSourceExe=WebCodeCli.exe" `
    $installerScriptPath

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed."
}

if (-not (Test-Path $installerPath)) {
    throw "Expected installer was not found at $installerPath"
}

$assets = @($portableZipPath, $installerPath)
$checksumLines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $asset).Hash.ToLowerInvariant()
    "$hash *$(Split-Path -Path $asset -Leaf)"
}
[System.IO.File]::WriteAllLines($checksumsPath, $checksumLines, [System.Text.UTF8Encoding]::new($false))

$releaseNotes = @"
# WebCode $versionTag

## Assets
- $([System.IO.Path]::GetFileName($installerPath))
- $([System.IO.Path]::GetFileName($portableZipPath))
- $([System.IO.Path]::GetFileName($checksumsPath))

## Packaging notes
- Built from commit $(git -C $repoRoot rev-parse --short HEAD)
- Self-contained $RuntimeIdentifier build, no separate .NET runtime installation required
- The installer keeps an existing appsettings.json on upgrade
- Default install path is %LOCALAPPDATA%\Programs\WebCode
- Default runtime data paths are data/ and workspaces/ under the install directory
- After launch, open $launchUrl in the browser

## Feishu reply delivery
- Reply TTS has been removed from WebCode.
- Feishu full-reply and conclusion-reply outputs are stored as cloud documents.
- Users can listen to the generated reply through Feishu document audio.
"@
[System.IO.File]::WriteAllText($releaseNotesPath, $releaseNotes.Trim() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Build completed."
Write-Host "Installer: $installerPath"
Write-Host "Portable ZIP: $portableZipPath"
Write-Host "Checksums: $checksumsPath"
Write-Host "Release notes: $releaseNotesPath"
