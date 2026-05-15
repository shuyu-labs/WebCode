param(
    [string]$StorageRoot = "",
    [int]$Port = 5058,
    [string]$DefaultVoiceId = "zh_47",
    [string]$Provider = "cpu",
    [int]$NumThreads = 4,
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
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

function Test-DriveWritable {
    param([System.IO.DriveInfo]$Drive)

    if (-not $Drive.IsReady) {
        return $false
    }

    $probeRoot = Join-Path $Drive.RootDirectory.FullName ".webcode-kokoro-probe-$([Guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null
        $probeFile = Join-Path $probeRoot "probe.tmp"
        [System.IO.File]::WriteAllText($probeFile, "probe")
        return $true
    }
    catch {
        return $false
    }
    finally {
        if (Test-Path -LiteralPath $probeRoot) {
            Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Resolve-DefaultStorageRoot {
    if (-not (Test-IsWindows)) {
        return "/data/webcode/kokoro"
    }

    $systemDriveRoot = Get-WindowsSystemDriveRoot
    $dataDrive = [System.IO.DriveInfo]::GetDrives() |
        Where-Object {
            $_.DriveType -eq [System.IO.DriveType]::Fixed -and
            $_.IsReady -and
            -not (Test-IsSameWindowsDrive $_.RootDirectory.FullName $systemDriveRoot) -and
            (Test-DriveWritable $_)
        } |
        Sort-Object Name |
        Select-Object -First 1

    if ($null -eq $dataDrive) {
        throw "Kokoro/sherpa-onnx TTS cannot be installed on this Windows machine because only the system drive '$systemDriveRoot' is writable. Attach or map a writable non-system drive, then pass -StorageRoot such as E:\WebCodeData\Kokoro."
    }

    return Join-Path $dataDrive.RootDirectory.FullName "WebCodeData\Kokoro"
}

function Get-BundledPythonHome {
    param([string]$StorageRoot)

    $pythonRoot = Join-Path $StorageRoot "python"
    if (-not (Test-Path $pythonRoot)) {
        return $null
    }

    $candidateHomes = Get-ChildItem -Path $pythonRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name
    foreach ($candidateHome in $candidateHomes) {
        $pythonExecutablePath = Join-Path $candidateHome.FullName "python.exe"
        if (Test-Path $pythonExecutablePath) {
            return $candidateHome.FullName
        }
    }

    return $null
}

function Repair-BundledVenvConfig {
    param(
        [string]$StorageRoot,
        [string]$BundledPythonHome
    )

    if ([string]::IsNullOrWhiteSpace($BundledPythonHome)) {
        return
    }

    $venvConfigPath = Join-Path $StorageRoot "venv\pyvenv.cfg"
    if (-not (Test-Path $venvConfigPath)) {
        return
    }

    $expectedPythonHome = [System.IO.Path]::GetFullPath($BundledPythonHome)
    $lines = Get-Content -Path $venvConfigPath
    $updated = $false
    $hasHomeEntry = $false

    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s*home\s*=') {
            $hasHomeEntry = $true
            $expectedLine = "home = $expectedPythonHome"
            if ($lines[$index] -ne $expectedLine) {
                $lines[$index] = $expectedLine
                $updated = $true
            }
        }
    }

    if (-not $hasHomeEntry) {
        $lines = @("home = $expectedPythonHome") + $lines
        $updated = $true
    }

    if ($updated) {
        [System.IO.File]::WriteAllLines($venvConfigPath, $lines, [System.Text.UTF8Encoding]::new($false))
    }
}

function Resolve-PythonCommand {
    param(
        [string]$StorageRoot,
        [string]$RequestedPython
    )

    $bundledPythonHome = Get-BundledPythonHome -StorageRoot $StorageRoot
    if (-not [string]::IsNullOrWhiteSpace($bundledPythonHome)) {
        Repair-BundledVenvConfig -StorageRoot $StorageRoot -BundledPythonHome $bundledPythonHome
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedPython) -and $RequestedPython -ne "python") {
        if (-not [System.IO.Path]::IsPathRooted($RequestedPython) -or (Test-Path $RequestedPython)) {
            return $RequestedPython
        }
    }

    $bundledVenvPythonPath = Join-Path $StorageRoot "venv\Scripts\python.exe"
    if (Test-Path $bundledVenvPythonPath) {
        return $bundledVenvPythonPath
    }

    if (-not [string]::IsNullOrWhiteSpace($bundledPythonHome)) {
        $bundledPythonPath = Join-Path $bundledPythonHome "python.exe"
        if (Test-Path $bundledPythonPath) {
            return $bundledPythonPath
        }
    }

    return "python"
}

if ([string]::IsNullOrWhiteSpace($StorageRoot)) {
    $StorageRoot = Resolve-DefaultStorageRoot
}

$resolvedRoot = [System.IO.Path]::GetFullPath($StorageRoot)

if ((Test-IsWindows) -and (Test-IsSameWindowsDrive $resolvedRoot (Get-WindowsSystemDriveRoot))) {
    throw "Refusing to use the Windows system drive for Kokoro/sherpa-onnx TTS storage. Configure a writable non-system drive such as E:\WebCodeData\Kokoro."
}

$directories = @{
    KOKORO_CACHE_ROOT = Join-Path $resolvedRoot "cache"
    TEMP = Join-Path $resolvedRoot "temp"
    TMP = Join-Path $resolvedRoot "temp"
    PIP_CACHE_DIR = Join-Path $resolvedRoot "cache\pip"
}

foreach ($entry in $directories.GetEnumerator()) {
    New-Item -ItemType Directory -Force -Path $entry.Value | Out-Null
    Set-Item -Path "Env:$($entry.Key)" -Value $entry.Value
}

New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot "logs") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot "models") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot "service") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot "venv") | Out-Null

$env:KOKORO_DEFAULT_VOICE_ID = $DefaultVoiceId
$env:KOKORO_PROVIDER = $Provider
$env:KOKORO_NUM_THREADS = "$NumThreads"
$env:KOKORO_HOST = "127.0.0.1"
$env:KOKORO_PORT = "$Port"
$env:KOKORO_STORAGE_ROOT = $resolvedRoot
$Python = Resolve-PythonCommand -StorageRoot $resolvedRoot -RequestedPython $Python

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $scriptRoot
try {
    & $Python -m uvicorn app:app --host 127.0.0.1 --port $Port
}
finally {
    Pop-Location
}
