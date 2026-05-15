using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 持久化进程信息
/// </summary>
public class PersistentProcessInfo : IDisposable
{
    public Process Process { get; set; } = default!;
    public string SessionId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsRunning => Process != null && !Process.HasExited;
    
    private bool _disposed = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Process != null && !Process.HasExited)
            {
                Process.Kill(true);
                Process.WaitForExit(5000);
            }
            Process?.Dispose();
        }
        catch
        {
            // 忽略清理错误
        }
    }
}

/// <summary>
/// 持久化进程管理器 - 为每个会话维护长期运行的CLI进程
/// </summary>
public class PersistentProcessManager : IDisposable
{
    private readonly ILogger<PersistentProcessManager> _logger;
    private readonly ConcurrentDictionary<string, PersistentProcessInfo> _processes = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _processIdleTimeout = TimeSpan.FromMinutes(30); // 30分钟无活动则清理
    private bool _disposed = false;

    public PersistentProcessManager(ILogger<PersistentProcessManager> logger)
    {
        _logger = logger;
        
        // 启动定期清理任务（每5分钟检查一次）
        _cleanupTimer = new Timer(CleanupIdleProcesses, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// 获取或创建会话的持久化进程
    /// </summary>
    public PersistentProcessInfo GetOrCreateProcess(
        string sessionId, 
        string toolId, 
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables = null)
    {
        var key = GetProcessKey(sessionId, toolId);

        // 尝试获取现有进程
        if (_processes.TryGetValue(key, out var existingProcess))
        {
            if (existingProcess.IsRunning)
            {
                existingProcess.LastUsedAt = DateTime.UtcNow;
                _logger.LogInformation("复用现有进程: Session={SessionId}, Tool={ToolId}, PID={ProcessId}", 
                    sessionId, toolId, existingProcess.Process.Id);
                return existingProcess;
            }
            else
            {
                // 进程已退出，移除并重新创建
                _logger.LogWarning("现有进程已退出，将重新创建: Session={SessionId}, Tool={ToolId}", sessionId, toolId);
                _processes.TryRemove(key, out _);
                existingProcess.Dispose();
            }
        }

        // 创建新进程
        var processInfo = CreateProcess(sessionId, toolId, tool, workingDirectory, environmentVariables);
        _processes[key] = processInfo;
        
        _logger.LogInformation("创建新的持久化进程: Session={SessionId}, Tool={ToolId}, PID={ProcessId}", 
            sessionId, toolId, processInfo.Process.Id);
        
        return processInfo;
    }

    /// <summary>
    /// 创建新的CLI进程（交互模式）
    /// </summary>
    private PersistentProcessInfo CreateProcess(
        string sessionId,
        string toolId,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool.Command,
            // 使用交互模式参数（如果工具支持）
            Arguments = tool.PersistentModeArguments ?? tool.ArgumentTemplate.Replace("{prompt}", ""),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };

        // 关键：强制移除 CLAUDECODE 环境变量，避免子进程检测到嵌套 Claude Code 会话
        // 当 Webcode 本身运行在 Claude Code 中时，会继承此变量，导致子进程启动失败
        if (startInfo.EnvironmentVariables.ContainsKey("CLAUDECODE"))
        {
            startInfo.EnvironmentVariables.Remove("CLAUDECODE");
            _logger.LogDebug("移除 CLAUDECODE 环境变量，避免嵌套会话检测");
        }

        // 设置环境变量
        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            foreach (var kvp in environmentVariables)
            {
                // 空字符串表示显式移除环境变量（用于取消继承父进程的环境变量）
                if (string.IsNullOrEmpty(kvp.Value))
                {
                    startInfo.EnvironmentVariables.Remove(kvp.Key);
                    _logger.LogDebug("移除环境变量: {Key}", kvp.Key);
                    continue;
                }
                // 非空值正常设置
                startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
            
            // 在 Windows 上设置编码相关环境变量(仅在已修改环境变量时)
            if (OperatingSystem.IsWindows())
            {
                if (!startInfo.EnvironmentVariables.ContainsKey("PYTHONIOENCODING"))
                {
                    startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                }
                if (!startInfo.EnvironmentVariables.ContainsKey("PYTHONLEGACYWINDOWSSTDIO"))
                {
                    startInfo.EnvironmentVariables["PYTHONLEGACYWINDOWSSTDIO"] = "utf-8";
                }
                if (!startInfo.EnvironmentVariables.ContainsKey("PYTHONLEGACYWINDOWSFSENCODING"))
                {
                    startInfo.EnvironmentVariables["PYTHONLEGACYWINDOWSFSENCODING"] = "utf-8";
                }
            }
        }

        var process = new Process { StartInfo = startInfo };
        
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动进程");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"启动进程失败: {ex.Message}", ex);
        }

        return new PersistentProcessInfo
        {
            Process = process,
            SessionId = sessionId,
            ToolId = toolId,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 向进程发送输入
    /// </summary>
    public async Task SendInputAsync(PersistentProcessInfo processInfo, string input, CancellationToken cancellationToken = default)
    {
        if (!processInfo.IsRunning)
        {
            throw new InvalidOperationException("进程未运行");
        }

        await processInfo.Process.StandardInput.WriteLineAsync(input);
        await processInfo.Process.StandardInput.FlushAsync();
        processInfo.LastUsedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 清理指定会话和工具的持久化进程
    /// </summary>
    public bool CleanupProcess(string sessionId, string toolId)
    {
        var key = GetProcessKey(sessionId, toolId);
        if (!_processes.TryRemove(key, out var processInfo))
        {
            return false;
        }

        _logger.LogInformation("清理会话进程: Session={SessionId}, Tool={ToolId}, PID={ProcessId}",
            processInfo.SessionId, processInfo.ToolId, processInfo.Process?.Id);
        processInfo.Dispose();
        return true;
    }

    /// <summary>
    /// 清理指定会话的进程
    /// </summary>
    public void CleanupSessionProcesses(string sessionId)
    {
        var keysToRemove = _processes.Keys.Where(k => k.StartsWith($"{sessionId}:")).ToList();
        
        foreach (var key in keysToRemove)
        {
            if (_processes.TryRemove(key, out var processInfo))
            {
                _logger.LogInformation("清理会话进程: Session={SessionId}, Tool={ToolId}, PID={ProcessId}",
                    processInfo.SessionId, processInfo.ToolId, processInfo.Process?.Id);
                processInfo.Dispose();
            }
        }
    }

    /// <summary>
    /// 清理空闲进程
    /// </summary>
    private void CleanupIdleProcesses(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var keysToRemove = new List<string>();

            foreach (var kvp in _processes)
            {
                var processInfo = kvp.Value;
                
                // 检查进程是否已退出或空闲时间过长
                if (!processInfo.IsRunning || (now - processInfo.LastUsedAt) > _processIdleTimeout)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                if (_processes.TryRemove(key, out var processInfo))
                {
                    _logger.LogInformation("清理空闲或已退出的进程: Session={SessionId}, Tool={ToolId}, LastUsed={LastUsed}",
                        processInfo.SessionId, processInfo.ToolId, processInfo.LastUsedAt);
                    processInfo.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理空闲进程时发生错误");
        }
    }

    private string GetProcessKey(string sessionId, string toolId)
    {
        return $"{sessionId}:{toolId}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer?.Dispose();

        // 清理所有进程
        foreach (var processInfo in _processes.Values)
        {
            processInfo.Dispose();
        }
        _processes.Clear();
    }
}
