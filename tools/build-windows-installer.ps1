[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$ProjectPath,
    [string]$OutputRoot,
    [string]$ReplyTtsSourceRoot,
    [string]$ReplyTtsFfmpegExecutablePath
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

    if ($settings.ContainsKey("DBConnection")) {
        $dbConnection = $settings["DBConnection"]
        if ($dbConnection) {
            $dbConnection["ConnectionStrings"] = "Data Source=data/WebCodeCli.db"
            $dbConnection["VectorConnection"] = "data/WebCodeCliMem.db"
        }
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

function Copy-ReplyTtsServiceAssets {
    param(
        [string]$RepoRoot,
        [string]$PublishDirectory
    )

    $sourceRoot = Join-Path $RepoRoot "tools\sherpa-kokoro-service"
    if (-not (Test-Path $sourceRoot)) {
        throw "Reply TTS service assets were not found at $sourceRoot"
    }

    $destinationRoot = Join-Path $PublishDirectory "tools\sherpa-kokoro-service"
    if (Test-Path $destinationRoot) {
        Remove-Item -Recurse -Force $destinationRoot
    }

    New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null

    foreach ($relativePath in @(
        "README.md",
        "requirements.txt",
        "app.py",
        "start.ps1",
        "start.sh")) {
        $sourcePath = Join-Path $sourceRoot $relativePath
        if (-not (Test-Path $sourcePath)) {
            throw "Required Reply TTS service asset was not found at $sourcePath"
        }

        $destinationPath = Join-Path $destinationRoot $relativePath
        $destinationParent = Split-Path -Parent $destinationPath
        if (-not (Test-Path $destinationParent)) {
            New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        }

        Copy-Item -Path $sourcePath -Destination $destinationPath -Force
    }
}

function Get-WindowsSystemDriveRoot {
    $systemRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    if (-not [string]::IsNullOrWhiteSpace($systemRoot)) {
        return [System.IO.Path]::GetPathRoot($systemRoot)
    }

    $systemDrive = [Environment]::GetEnvironmentVariable("SystemDrive")
    if ([string]::IsNullOrWhiteSpace($systemDrive)) {
        return "C:\"
    }

    return "$($systemDrive.TrimEnd('\'))\"
}

function Test-IsSameWindowsDrive {
    param(
        [string]$Left,
        [string]$Right
    )

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }

    $leftRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Left))
    $rightRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Right))

    return $leftRoot.TrimEnd('\') -ieq $rightRoot.TrimEnd('\')
}

function Resolve-ReplyTtsSourceRoot {
    param([string]$RequestedSourceRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedSourceRoot)) {
        if (-not (Test-Path $RequestedSourceRoot)) {
            throw "Reply TTS bundle source root was not found at $RequestedSourceRoot"
        }

        return (Resolve-Path $RequestedSourceRoot).Path
    }

    $systemDriveRoot = Get-WindowsSystemDriveRoot
    $candidateRoots = [System.IO.DriveInfo]::GetDrives() |
        Where-Object {
            $_.DriveType -eq [System.IO.DriveType]::Fixed -and
            $_.IsReady -and
            -not (Test-IsSameWindowsDrive $_.RootDirectory.FullName $systemDriveRoot)
        } |
        Sort-Object Name |
        ForEach-Object { Join-Path $_.RootDirectory.FullName "WebCodeData\Kokoro" }

    foreach ($candidateRoot in $candidateRoots) {
        if (
            (Test-Path $candidateRoot) -and
            (Test-Path (Join-Path $candidateRoot "models\kokoro-int8-multi-lang-v1_1")) -and
            (Test-Path (Join-Path $candidateRoot "venv\Scripts\python.exe")) -and
            (Test-Path (Join-Path $candidateRoot "python"))
        ) {
            return (Resolve-Path $candidateRoot).Path
        }
    }

    throw "Reply TTS bundle source root was not found on any writable non-system fixed drive. Pass -ReplyTtsSourceRoot explicitly."
}

function Resolve-ReplyTtsFfmpegExecutablePath {
    param(
        [string]$RequestedExecutablePath,
        [string]$SourceRoot
    )

    $candidatePaths = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($RequestedExecutablePath)) {
        $candidatePaths.Add($RequestedExecutablePath)
    }

    $candidatePaths.Add((Join-Path $SourceRoot "ffmpeg\bin\ffmpeg.exe"))

    $ffmpegCommand = Get-Command ffmpeg.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($ffmpegCommand) {
        $candidatePaths.Add($ffmpegCommand.Source)
    }

    $candidatePaths.Add("C:\Program Files\ImageMagick-7.1.0-Q16\ffmpeg.exe")

    foreach ($candidatePath in $candidatePaths | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($candidatePath) -and (Test-Path $candidatePath)) {
            return (Resolve-Path $candidatePath).Path
        }
    }

    throw "Reply TTS ffmpeg executable was not found. Pass -ReplyTtsFfmpegExecutablePath explicitly."
}

function Get-ReplyTtsBundledPythonHome {
    param([string]$PythonRoot)

    if (-not (Test-Path $PythonRoot)) {
        throw "Reply TTS python root was not found at $PythonRoot"
    }

    $bundledPythonHome = Get-ChildItem -Path $PythonRoot -Directory |
        Sort-Object Name |
        Where-Object { Test-Path (Join-Path $_.FullName "python.exe") } |
        Select-Object -First 1

    if ($null -eq $bundledPythonHome) {
        throw "Reply TTS python root at $PythonRoot does not contain a bundled python.exe"
    }

    return $bundledPythonHome.FullName
}

function Get-ReplyTtsBundleSourceLayout {
    param(
        [string]$RequestedSourceRoot,
        [string]$RequestedFfmpegExecutablePath
    )

    $sourceRoot = Resolve-ReplyTtsSourceRoot -RequestedSourceRoot $RequestedSourceRoot
    $modelRoot = Join-Path $sourceRoot "models\kokoro-int8-multi-lang-v1_1"
    $venvRoot = Join-Path $sourceRoot "venv"
    $pythonRoot = Join-Path $sourceRoot "python"
    $venvPythonPath = Join-Path $venvRoot "Scripts\python.exe"
    $venvConfigPath = Join-Path $venvRoot "pyvenv.cfg"

    if (-not (Test-Path $modelRoot)) {
        throw "Reply TTS model directory was not found at $modelRoot"
    }

    foreach ($requiredRelativePath in @(
        "model.int8.onnx",
        "voices.bin",
        "tokens.txt",
        "lexicon-us-en.txt",
        "lexicon-zh.txt",
        "date-zh.fst",
        "phone-zh.fst",
        "number-zh.fst",
        "espeak-ng-data")) {
        $requiredPath = Join-Path $modelRoot $requiredRelativePath
        if (-not (Test-Path $requiredPath)) {
            throw "Reply TTS model directory is incomplete. Missing $requiredPath"
        }
    }

    if (-not (Test-Path $venvPythonPath)) {
        throw "Reply TTS bundled venv python was not found at $venvPythonPath"
    }

    if (-not (Test-Path $venvConfigPath)) {
        throw "Reply TTS bundled venv config was not found at $venvConfigPath"
    }

    $bundledPythonHome = Get-ReplyTtsBundledPythonHome -PythonRoot $pythonRoot
    $ffmpegExecutablePath = Resolve-ReplyTtsFfmpegExecutablePath `
        -RequestedExecutablePath $RequestedFfmpegExecutablePath `
        -SourceRoot $sourceRoot

    return [pscustomobject]@{
        SourceRoot            = $sourceRoot
        ModelRoot             = $modelRoot
        VenvRoot              = $venvRoot
        PythonRoot            = $pythonRoot
        BundledPythonHome     = $bundledPythonHome
        FfmpegExecutablePath  = $ffmpegExecutablePath
    }
}

function Copy-DirectoryTree {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path $Source)) {
        throw "Directory copy source was not found at $Source"
    }

    $parentPath = Split-Path -Parent $Destination
    if (-not [string]::IsNullOrWhiteSpace($parentPath) -and -not (Test-Path $parentPath)) {
        New-Item -ItemType Directory -Force -Path $parentPath | Out-Null
    }

    if (Test-Path $Destination) {
        Remove-Item -Recurse -Force $Destination
    }

    Copy-Item -Path $Source -Destination $Destination -Recurse -Force
}

function Copy-ReplyTtsBundleAssets {
    param(
        [psobject]$SourceLayout,
        [string]$BundleDirectory
    )

    if (Test-Path $BundleDirectory) {
        Remove-Item -Recurse -Force $BundleDirectory
    }

    New-Item -ItemType Directory -Force -Path $BundleDirectory | Out-Null

    Copy-DirectoryTree `
        -Source $SourceLayout.ModelRoot `
        -Destination (Join-Path $BundleDirectory "models\kokoro-int8-multi-lang-v1_1")
    Copy-DirectoryTree `
        -Source $SourceLayout.PythonRoot `
        -Destination (Join-Path $BundleDirectory "python")
    Copy-DirectoryTree `
        -Source $SourceLayout.VenvRoot `
        -Destination (Join-Path $BundleDirectory "venv")

    $ffmpegBinDirectory = Join-Path $BundleDirectory "ffmpeg\bin"
    New-Item -ItemType Directory -Force -Path $ffmpegBinDirectory | Out-Null
    Copy-Item -Path $SourceLayout.FfmpegExecutablePath -Destination (Join-Path $ffmpegBinDirectory "ffmpeg.exe") -Force

    foreach ($relativeDirectory in @("cache", "logs", "service", "temp")) {
        New-Item -ItemType Directory -Force -Path (Join-Path $BundleDirectory $relativeDirectory) | Out-Null
    }
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
$replyTtsSourceLayout = Get-ReplyTtsBundleSourceLayout `
    -RequestedSourceRoot $ReplyTtsSourceRoot `
    -RequestedFfmpegExecutablePath $ReplyTtsFfmpegExecutablePath

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
$ttsBundleDirectory = Join-Path $releaseRoot "tts-bundle"
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
    --self-contained true `
    -o $publishDirectory `
    /p:PublishSingleFile=false `
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
Copy-ReplyTtsServiceAssets -RepoRoot $repoRoot -PublishDirectory $publishDirectory
Copy-ReplyTtsBundleAssets -SourceLayout $replyTtsSourceLayout -BundleDirectory $ttsBundleDirectory
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
    "/DTtsBundleDir=$ttsBundleDirectory" `
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
- Includes the local Kokoro/sherpa-onnx Reply TTS wrapper under tools/sherpa-kokoro-service
- The Windows installer deploys the bundled Reply TTS model, ffmpeg, Python runtime, and venv to a writable non-system drive such as E:\WebCodeData\Kokoro
- The Windows installer stops with an error if only the Windows system drive is writable
- After launch, open $launchUrl in the browser
"@
[System.IO.File]::WriteAllText($releaseNotesPath, $releaseNotes.Trim() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Build completed."
Write-Host "Installer: $installerPath"
Write-Host "Portable ZIP: $portableZipPath"
Write-Host "Checksums: $checksumsPath"
Write-Host "Release notes: $releaseNotesPath"
Write-Host "Reply TTS source root: $($replyTtsSourceLayout.SourceRoot)"
