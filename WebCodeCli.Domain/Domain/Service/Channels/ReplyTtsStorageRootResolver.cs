using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public sealed class ReplyTtsStorageRootResolver
{
    private const string NonWindowsDefaultRoot = "/data/webcode/kokoro";

    private readonly IOptionsMonitor<FeishuReplyTtsOptions> _optionsMonitor;
    private readonly IReplyTtsHostEnvironment _hostEnvironment;

    public ReplyTtsStorageRootResolver(
        IOptionsMonitor<FeishuReplyTtsOptions> optionsMonitor,
        IReplyTtsHostEnvironment? hostEnvironment = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _hostEnvironment = hostEnvironment ?? new SystemReplyTtsHostEnvironment();
    }

    public FeishuReplyTtsHealthStatus Resolve()
    {
        var options = _optionsMonitor.CurrentValue ?? new FeishuReplyTtsOptions();
        var explicitRoot = options.TtsStorageRoot?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var useWindowsPaths = UsesWindowsSeparators(explicitRoot) || _hostEnvironment.IsWindows;
            if (useWindowsPaths && IsSameDrive(explicitRoot, _hostEnvironment.SystemDriveRoot))
            {
                return new FeishuReplyTtsHealthStatus
                {
                    IsAvailable = false,
                    Message = "Feishu reply TTS storage is unavailable because Kokoro/sherpa-onnx must be installed on a non-system drive. Set FeishuReplyTts:TtsStorageRoot to a non-C drive."
                };
            }

            return CreateAvailable(
                NormalizeStorageRoot(explicitRoot, useWindowsPaths),
                "Using configured Feishu reply TTS storage root.",
                useWindowsPaths);
        }

        if (!_hostEnvironment.IsWindows)
        {
            return CreateAvailable(
                NonWindowsDefaultRoot,
                "Using default non-Windows Feishu reply TTS storage root.",
                useWindowsPaths: false);
        }

        var systemDriveRoot = NormalizeDriveRoot(_hostEnvironment.SystemDriveRoot);
        var writableDrives = _hostEnvironment.GetFixedDrives()
            .Where(d => d.IsReady && d.IsWritable)
            .ToList();

        var existingNonSystemDrive = writableDrives.FirstOrDefault(d =>
        {
            if (IsSameDrive(d.RootPath, systemDriveRoot))
            {
                return false;
            }

            return HasWindowsInstallEvidence(BuildWindowsStorageRoot(d.RootPath));
        });
        if (existingNonSystemDrive is not null)
        {
            var resolvedRoot = BuildWindowsStorageRoot(existingNonSystemDrive.RootPath);
            return CreateAvailable(
                resolvedRoot,
                $"Using existing Feishu reply TTS storage root on writable non-system drive '{NormalizeDriveRoot(existingNonSystemDrive.RootPath)}'.",
                useWindowsPaths: true);
        }

        var nonSystemDrive = writableDrives.FirstOrDefault(d => !IsSameDrive(d.RootPath, systemDriveRoot));
        if (nonSystemDrive is not null)
        {
            var resolvedRoot = BuildWindowsStorageRoot(nonSystemDrive.RootPath);
            return CreateAvailable(
                resolvedRoot,
                $"Using writable non-system drive '{NormalizeDriveRoot(nonSystemDrive.RootPath)}' for Feishu reply TTS storage.",
                useWindowsPaths: true);
        }

        var systemDrive = writableDrives.FirstOrDefault(d => IsSameDrive(d.RootPath, systemDriveRoot));
        if (systemDrive is not null)
        {
            var driveLabel = NormalizeDriveRoot(systemDrive.RootPath);
            return new FeishuReplyTtsHealthStatus
            {
                IsAvailable = false,
                Message = $"Feishu reply TTS storage is unavailable because only the Windows system drive '{driveLabel}' is writable. Attach a writable non-system drive and set FeishuReplyTts:TtsStorageRoot to that drive."
            };
        }

        return new FeishuReplyTtsHealthStatus
        {
            IsAvailable = false,
            Message = "Feishu reply TTS storage is unavailable because no writable fixed drive was found on Windows. Set FeishuReplyTts:TtsStorageRoot explicitly or attach a writable data drive."
        };
    }

    private static FeishuReplyTtsHealthStatus CreateAvailable(string storageRoot, string message, bool useWindowsPaths)
    {
        return new FeishuReplyTtsHealthStatus
        {
            IsAvailable = true,
            StorageRoot = storageRoot,
            Message = message,
            ModelsRoot = AppendSegment(storageRoot, "models", useWindowsPaths),
            CacheRoot = AppendSegment(storageRoot, "cache", useWindowsPaths),
            TempRoot = AppendSegment(storageRoot, "temp", useWindowsPaths),
            LogsRoot = AppendSegment(storageRoot, "logs", useWindowsPaths),
            VenvRoot = AppendSegment(storageRoot, "venv", useWindowsPaths)
        };
    }

    private static string BuildWindowsStorageRoot(string driveRoot)
    {
        return AppendSegment(AppendSegment(NormalizeDriveRoot(driveRoot), "WebCodeData", useWindowsPaths: true), "Kokoro", useWindowsPaths: true);
    }

    private bool HasWindowsInstallEvidence(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot) || !_hostEnvironment.DirectoryExists(storageRoot))
        {
            return false;
        }

        var ffmpegPath = AppendSegment(AppendSegment(AppendSegment(storageRoot, "ffmpeg", useWindowsPaths: true), "bin", useWindowsPaths: true), "ffmpeg.exe", useWindowsPaths: true);
        if (_hostEnvironment.FileExists(ffmpegPath))
        {
            return true;
        }

        var modelsRoot = AppendSegment(storageRoot, "models", useWindowsPaths: true);
        if (_hostEnvironment.DirectoryExists(modelsRoot))
        {
            return true;
        }

        var venvRoot = AppendSegment(storageRoot, "venv", useWindowsPaths: true);
        if (_hostEnvironment.DirectoryExists(venvRoot))
        {
            return true;
        }

        var serviceRoot = AppendSegment(storageRoot, "service", useWindowsPaths: true);
        return _hostEnvironment.DirectoryExists(serviceRoot);
    }

    private static string AppendSegment(string root, string segment, bool useWindowsPaths)
    {
        var separator = useWindowsPaths ? '\\' : '/';
        var normalizedRoot = NormalizeStorageRoot(root, useWindowsPaths);
        var normalizedSegment = segment.Trim().Trim('\\', '/');

        if (normalizedRoot[^1] == separator)
        {
            return normalizedRoot + normalizedSegment;
        }

        return normalizedRoot + separator + normalizedSegment;
    }

    private static bool UsesWindowsSeparators(string path)
    {
        return path.Contains('\\', StringComparison.Ordinal) ||
               (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');
    }

    private static bool IsSameDrive(string driveRoot, string systemDriveRoot)
    {
        return string.Equals(
            NormalizeDriveRootForComparison(driveRoot),
            NormalizeDriveRootForComparison(systemDriveRoot),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDriveRootForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('/', '\\');
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            return $"{char.ToUpperInvariant(normalized[0])}:\\";
        }

        return NormalizeDriveRoot(normalized);
    }

    private static string NormalizeDriveRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        var normalized = root.Trim().Replace('/', '\\');
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            return normalized + "\\";
        }

        normalized = normalized.TrimEnd('\\');
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            return normalized + "\\";
        }

        if (!normalized.EndsWith('\\'))
        {
            normalized += "\\";
        }

        return normalized;
    }

    private static string NormalizeStorageRoot(string path, bool useWindowsPaths)
    {
        var separator = useWindowsPaths ? '\\' : '/';
        var alternateSeparator = useWindowsPaths ? '/' : '\\';
        var normalized = path.Trim().Replace(alternateSeparator, separator);

        if (useWindowsPaths && IsWindowsDriveDesignator(normalized))
        {
            return normalized + "\\";
        }

        while (normalized.Length > 1 && normalized.EndsWith(separator))
        {
            if (!useWindowsPaths && normalized == "/")
            {
                break;
            }

            if (useWindowsPaths && normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '\\')
            {
                break;
            }

            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static bool IsWindowsDriveDesignator(string path)
    {
        return path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    private sealed class SystemReplyTtsHostEnvironment : IReplyTtsHostEnvironment
    {
        public bool IsWindows => OperatingSystem.IsWindows();

        public string? SystemDriveRoot
        {
            get
            {
                if (!IsWindows)
                {
                    return null;
                }

                var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return string.IsNullOrWhiteSpace(systemDirectory)
                    ? Environment.GetEnvironmentVariable("SystemDrive")
                    : Path.GetPathRoot(systemDirectory);
            }
        }

        public IReadOnlyList<ReplyTtsDriveDescriptor> GetFixedDrives()
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed)
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Select(drive => new ReplyTtsDriveDescriptor(
                    drive.RootDirectory.FullName,
                    drive.IsReady,
                    CanWriteToDrive(drive)))
                .ToArray();
        }

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        private static bool CanWriteToDrive(DriveInfo drive)
        {
            if (!drive.IsReady)
            {
                return false;
            }

            var probeToken = Guid.NewGuid().ToString("N");
            var probeSandboxRoot = BuildProbeSandboxRoot(drive.RootDirectory.FullName, probeToken);
            var probeDirectory = BuildProbeTargetDirectory(drive.RootDirectory.FullName, probeToken);
            var probeFilePath = Path.Combine(probeDirectory, "probe.tmp");

            try
            {
                Directory.CreateDirectory(probeDirectory);

                using var stream = new FileStream(
                    probeFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                TryDeleteProbePath(probeFilePath);
                TryDeleteProbeDirectory(probeSandboxRoot);
            }
        }

        private static string BuildProbeSandboxRoot(string driveRoot, string probeToken)
        {
            return Path.Combine(
                NormalizeDriveRoot(driveRoot),
                $".webcode-feishu-reply-tts-probe-{probeToken}");
        }

        private static string BuildProbeTargetDirectory(string driveRoot, string probeToken)
        {
            return Path.Combine(
                BuildProbeSandboxRoot(driveRoot, probeToken),
                "webcode",
                "kokoro");
        }

        private static void TryDeleteProbePath(string probeFilePath)
        {
            try
            {
                if (File.Exists(probeFilePath))
                {
                    File.Delete(probeFilePath);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteProbeDirectory(string probeDirectory)
        {
            try
            {
                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}

public interface IReplyTtsHostEnvironment
{
    bool IsWindows { get; }

    string? SystemDriveRoot { get; }

    IReadOnlyList<ReplyTtsDriveDescriptor> GetFixedDrives();

    bool DirectoryExists(string path);

    bool FileExists(string path);
}

public sealed class ReplyTtsDriveDescriptor
{
    public ReplyTtsDriveDescriptor(string rootPath, bool isReady, bool isWritable)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        IsReady = isReady;
        IsWritable = isWritable;
    }

    public string RootPath { get; }

    public bool IsReady { get; }

    public bool IsWritable { get; }
}
