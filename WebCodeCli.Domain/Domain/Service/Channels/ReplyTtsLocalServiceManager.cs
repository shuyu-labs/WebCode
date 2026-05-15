using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(IReplyTtsLocalServiceManager), ServiceLifetime.Singleton)]
public sealed class ReplyTtsLocalServiceManager : IReplyTtsLocalServiceManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FeishuReplyTtsOptions> _optionsMonitor;
    private readonly ILogger<ReplyTtsLocalServiceManager> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _startedProcess;

    public ReplyTtsLocalServiceManager(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FeishuReplyTtsOptions> optionsMonitor,
        ILogger<ReplyTtsLocalServiceManager> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FeishuReplyTtsHealthStatus> EnsureStartedAsync(
        FeishuReplyTtsHealthStatus storageHealth,
        CancellationToken cancellationToken = default)
    {
        if (storageHealth == null)
        {
            throw new ArgumentNullException(nameof(storageHealth));
        }

        if (!storageHealth.IsAvailable || string.IsNullOrWhiteSpace(storageHealth.StorageRoot))
        {
            return storageHealth;
        }

        var existingHealth = await TryGetServiceHealthAsync(cancellationToken);
        if (existingHealth?.IsAvailable == true)
        {
            return existingHealth;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            existingHealth = await TryGetServiceHealthAsync(cancellationToken);
            if (existingHealth?.IsAvailable == true)
            {
                return existingHealth;
            }

            var startInfoResult = CreateStartInfo(storageHealth);
            if (startInfoResult.StartInfo == null)
            {
                return Unavailable(startInfoResult.Message, "auto-start-unavailable");
            }

            try
            {
                _startedProcess = Process.Start(startInfoResult.StartInfo);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start local Kokoro/sherpa-onnx service.");
                return Unavailable($"Failed to start local Kokoro/sherpa-onnx service: {ex.Message}", "start-failed");
            }

            if (_startedProcess == null)
            {
                return Unavailable("Failed to start local Kokoro/sherpa-onnx service: process was not created.", "start-failed");
            }

            _logger.LogInformation(
                "Started local Kokoro/sherpa-onnx service process. Pid={ProcessId}",
                _startedProcess.Id);

            return await WaitForServiceReadyAsync(_startedProcess, cancellationToken);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<FeishuReplyTtsHealthStatus> WaitForServiceReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _optionsMonitor.CurrentValue.TtsServiceStartupTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        FeishuReplyTtsHealthStatus? lastHealth = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lastHealth = await TryGetServiceHealthAsync(cancellationToken);
            if (lastHealth?.IsAvailable == true)
            {
                return lastHealth;
            }

            if (process.HasExited)
            {
                return Unavailable(
                    $"Local Kokoro/sherpa-onnx service exited before becoming healthy. ExitCode={process.ExitCode}.",
                    "start-exited");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return Unavailable(
            lastHealth == null
                ? $"Local Kokoro/sherpa-onnx service did not become healthy within {timeout.TotalSeconds:0} seconds."
                : $"Local Kokoro/sherpa-onnx service did not become healthy within {timeout.TotalSeconds:0} seconds: {lastHealth.Message}",
            "startup-timeout");
    }

    private async Task<FeishuReplyTtsHealthStatus?> TryGetServiceHealthAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ttsClient = scope.ServiceProvider.GetRequiredService<ISherpaKokoroTtsClient>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            return await ttsClient.GetHealthAsync(timeoutCts.Token);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            _logger.LogDebug(ex, "Local Kokoro/sherpa-onnx health probe failed.");
            return null;
        }
    }

    private StartInfoResult CreateStartInfo(FeishuReplyTtsHealthStatus storageHealth)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!TryGetLocalPort(options.TtsServiceBaseUrl, out var port, out var endpointMessage))
        {
            return StartInfoResult.Unavailable(endpointMessage);
        }

        var isWindows = OperatingSystem.IsWindows();
        var scriptPath = ResolveStartScriptPath(options, isWindows, out var scriptMessage);
        if (scriptPath == null)
        {
            return StartInfoResult.Unavailable(scriptMessage);
        }

        var startInfo = isWindows
            ? CreateWindowsStartInfo(scriptPath, storageHealth, options, port)
            : CreateUnixStartInfo(scriptPath, storageHealth, options, port);

        return StartInfoResult.Available(startInfo);
    }

    private static ProcessStartInfo CreateWindowsStartInfo(
        string scriptPath,
        FeishuReplyTtsHealthStatus storageHealth,
        FeishuReplyTtsOptions options,
        int port)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-StorageRoot");
        startInfo.ArgumentList.Add(storageHealth.StorageRoot!);
        startInfo.ArgumentList.Add("-Port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("-DefaultVoiceId");
        startInfo.ArgumentList.Add(ResolveDefaultVoiceId(options));
        startInfo.ArgumentList.Add("-Provider");
        startInfo.ArgumentList.Add(ResolveProvider(options));
        startInfo.ArgumentList.Add("-Python");
        startInfo.ArgumentList.Add(ResolvePythonPath(storageHealth, options, isWindows: true));

        return startInfo;
    }

    private static ProcessStartInfo CreateUnixStartInfo(
        string scriptPath,
        FeishuReplyTtsHealthStatus storageHealth,
        FeishuReplyTtsOptions options,
        int port)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/env")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(storageHealth.StorageRoot!);
        startInfo.Environment["KOKORO_PORT"] = port.ToString();
        startInfo.Environment["KOKORO_DEFAULT_VOICE_ID"] = ResolveDefaultVoiceId(options);
        startInfo.Environment["KOKORO_PROVIDER"] = ResolveProvider(options);

        return startInfo;
    }

    private static string ResolvePythonPath(
        FeishuReplyTtsHealthStatus storageHealth,
        FeishuReplyTtsOptions options,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(options.TtsServicePythonPath))
        {
            return options.TtsServicePythonPath.Trim();
        }

        if (!string.IsNullOrWhiteSpace(storageHealth.VenvRoot))
        {
            var pythonPath = isWindows
                ? Path.Combine(storageHealth.VenvRoot, "Scripts", "python.exe")
                : Path.Combine(storageHealth.VenvRoot, "bin", "python");
            if (File.Exists(pythonPath))
            {
                return pythonPath;
            }
        }

        return "python";
    }

    private static string ResolveProvider(FeishuReplyTtsOptions options)
    {
        return string.IsNullOrWhiteSpace(options.TtsPreferredDevice)
            ? "cpu"
            : options.TtsPreferredDevice.Trim();
    }

    private static string ResolveDefaultVoiceId(FeishuReplyTtsOptions options)
    {
        return string.IsNullOrWhiteSpace(options.TtsDefaultVoiceId)
            ? "zh_47"
            : options.TtsDefaultVoiceId.Trim();
    }

    private static string? ResolveStartScriptPath(
        FeishuReplyTtsOptions options,
        bool isWindows,
        out string message)
    {
        var configuredPath = options.TtsServiceStartScriptPath?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                message = string.Empty;
                return Path.GetFullPath(configuredPath);
            }

            message = $"Configured Kokoro/sherpa-onnx start script was not found: {configuredPath}.";
            return null;
        }

        var scriptName = isWindows ? "start.ps1" : "start.sh";
        foreach (var root in EnumerateSearchRoots())
        {
            var candidate = Path.Combine(root, "tools", "sherpa-kokoro-service", scriptName);
            if (File.Exists(candidate))
            {
                message = string.Empty;
                return candidate;
            }
        }

        message = $"Kokoro/sherpa-onnx start script '{scriptName}' was not found. Set FeishuReplyTts:TtsServiceStartScriptPath.";
        return null;
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            var directory = new DirectoryInfo(startPath);
            while (directory != null)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
    }

    private static bool TryGetLocalPort(string? baseUrl, out int port, out string message)
    {
        port = 0;
        var candidate = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://127.0.0.1:5058"
            : baseUrl.Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            message = $"Invalid FeishuReplyTts:TtsServiceBaseUrl: {candidate}.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Auto-start only supports local http TTS endpoints. Current endpoint is {uri}.";
            return false;
        }

        if (!IsLocalHost(uri.Host))
        {
            message = $"Auto-start only supports localhost TTS endpoints. Current host is {uri.Host}.";
            return false;
        }

        port = uri.Port;
        message = string.Empty;
        return true;
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private static FeishuReplyTtsHealthStatus Unavailable(string message, string serviceStatus)
    {
        return new FeishuReplyTtsHealthStatus
        {
            IsAvailable = false,
            Message = message,
            ServiceStatus = serviceStatus
        };
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested && exception is OperationCanceledException;
    }

    private sealed record StartInfoResult(ProcessStartInfo? StartInfo, string Message)
    {
        public static StartInfoResult Available(ProcessStartInfo startInfo)
        {
            return new StartInfoResult(startInfo, string.Empty);
        }

        public static StartInfoResult Unavailable(string message)
        {
            return new StartInfoResult(null, message);
        }
    }
}
