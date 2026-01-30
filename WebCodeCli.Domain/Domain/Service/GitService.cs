using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// Git 服务实现
/// </summary>
[ServiceDescription(typeof(IGitService), ServiceLifetime.Singleton)]
public class GitService : IGitService
{
    private readonly ILogger<GitService>? _logger;
    
    public GitService(ILogger<GitService>? logger = null)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// 检测工作区是否为 Git 仓库
    /// </summary>
    public bool IsGitRepository(string workspacePath)
    {
        try
        {
            return Repository.IsValid(workspacePath);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// 获取文件的提交历史
    /// </summary>
    public async Task<List<GitCommit>> GetFileHistoryAsync(string workspacePath, string filePath, int maxCount = 50)
    {
        return await Task.Run(() =>
        {
            var commits = new List<GitCommit>();
            
            try
            {
                if (!IsGitRepository(workspacePath))
                {
                    return commits;
                }
                
                using var repo = new Repository(workspacePath);
                
                // 获取文件的提交历史
                var filter = new CommitFilter
                {
                    SortBy = CommitSortStrategies.Time
                };
                
                var fileCommits = repo.Commits
                    .QueryBy(filePath, filter)
                    .Take(maxCount);
                
                foreach (var commit in fileCommits)
                {
                    commits.Add(new GitCommit
                    {
                        Hash = commit.Commit.Sha,
                        ShortHash = commit.Commit.Sha.Substring(0, 7),
                        Author = commit.Commit.Author.Name,
                        AuthorEmail = commit.Commit.Author.Email,
                        CommitDate = commit.Commit.Author.When.DateTime,
                        Message = commit.Commit.MessageShort,
                        ParentHashes = commit.Commit.Parents.Select(p => p.Sha).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取文件提交历史失败: {ex.Message}");
            }
            
            return commits;
        });
    }
    
    /// <summary>
    /// 获取特定版本的文件内容
    /// </summary>
    public async Task<string> GetFileContentAtCommitAsync(string workspacePath, string filePath, string commitHash)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!IsGitRepository(workspacePath))
                {
                    return string.Empty;
                }
                
                using var repo = new Repository(workspacePath);
                var commit = repo.Lookup<Commit>(commitHash);
                
                if (commit == null)
                {
                    return string.Empty;
                }
                
                // 标准化文件路径（使用正斜杠）
                var normalizedPath = filePath.Replace("\\", "/");
                
                var treeEntry = commit[normalizedPath];
                if (treeEntry == null || treeEntry.TargetType != TreeEntryTargetType.Blob)
                {
                    return string.Empty;
                }
                
                var blob = (Blob)treeEntry.Target;
                return blob.GetContentText();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取文件内容失败: {ex.Message}");
                return string.Empty;
            }
        });
    }
    
    /// <summary>
    /// 获取文件差异
    /// </summary>
    public async Task<GitDiffResult> GetFileDiffAsync(string workspacePath, string filePath, string fromCommit, string toCommit)
    {
        return await Task.Run(() =>
        {
            var result = new GitDiffResult();
            
            try
            {
                if (!IsGitRepository(workspacePath))
                {
                    return result;
                }
                
                using var repo = new Repository(workspacePath);
                
                // 获取两个版本的内容
                result.OldContent = GetFileContentAtCommitAsync(workspacePath, filePath, fromCommit).Result;
                result.NewContent = GetFileContentAtCommitAsync(workspacePath, filePath, toCommit).Result;
                
                // 使用 DiffPlex 计算差异
                var differ = new DiffPlex.Differ();
                var inlineDiffer = new DiffPlex.DiffBuilder.InlineDiffBuilder(differ);
                var diff = inlineDiffer.BuildDiffModel(result.OldContent, result.NewContent);
                
                int oldLineNum = 1;
                int newLineNum = 1;
                
                foreach (var line in diff.Lines)
                {
                    var diffLine = new DiffLine
                    {
                        Content = line.Text
                    };
                    
                    switch (line.Type)
                    {
                        case DiffPlex.DiffBuilder.Model.ChangeType.Unchanged:
                            diffLine.Type = DiffLineType.Unchanged;
                            diffLine.OldLineNumber = oldLineNum++;
                            diffLine.NewLineNumber = newLineNum++;
                            break;
                        case DiffPlex.DiffBuilder.Model.ChangeType.Deleted:
                            diffLine.Type = DiffLineType.Deleted;
                            diffLine.OldLineNumber = oldLineNum++;
                            result.DeletedLines++;
                            break;
                        case DiffPlex.DiffBuilder.Model.ChangeType.Inserted:
                            diffLine.Type = DiffLineType.Added;
                            diffLine.NewLineNumber = newLineNum++;
                            result.AddedLines++;
                            break;
                        case DiffPlex.DiffBuilder.Model.ChangeType.Modified:
                            diffLine.Type = DiffLineType.Modified;
                            diffLine.OldLineNumber = oldLineNum++;
                            diffLine.NewLineNumber = newLineNum++;
                            result.AddedLines++;
                            result.DeletedLines++;
                            break;
                    }
                    
                    result.DiffLines.Add(diffLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取文件差异失败: {ex.Message}");
            }
            
            return result;
        });
    }
    
    /// <summary>
    /// 获取工作区状态
    /// </summary>
    public async Task<GitStatus> GetWorkspaceStatusAsync(string workspacePath)
    {
        return await Task.Run(() =>
        {
            var status = new GitStatus();
            
            try
            {
                if (!IsGitRepository(workspacePath))
                {
                    return status;
                }
                
                using var repo = new Repository(workspacePath);
                var repoStatus = repo.RetrieveStatus();
                
                foreach (var item in repoStatus)
                {
                    if (item.State.HasFlag(FileStatus.ModifiedInWorkdir) || 
                        item.State.HasFlag(FileStatus.ModifiedInIndex))
                    {
                        status.ModifiedFiles.Add(item.FilePath);
                    }
                    
                    if (item.State.HasFlag(FileStatus.NewInWorkdir) || 
                        item.State.HasFlag(FileStatus.NewInIndex))
                    {
                        status.UntrackedFiles.Add(item.FilePath);
                    }
                    
                    if (item.State.HasFlag(FileStatus.DeletedFromWorkdir) || 
                        item.State.HasFlag(FileStatus.DeletedFromIndex))
                    {
                        status.DeletedFiles.Add(item.FilePath);
                    }
                    
                    if (item.State.HasFlag(FileStatus.NewInIndex) || 
                        item.State.HasFlag(FileStatus.ModifiedInIndex) ||
                        item.State.HasFlag(FileStatus.DeletedFromIndex))
                    {
                        status.StagedFiles.Add(item.FilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取工作区状态失败: {ex.Message}");
            }
            
            return status;
        });
    }
    
    /// <summary>
    /// 获取所有提交历史
    /// </summary>
    public async Task<List<GitCommit>> GetAllCommitsAsync(string workspacePath, int maxCount = 100)
    {
        return await Task.Run(() =>
        {
            var commits = new List<GitCommit>();
            
            try
            {
                if (!IsGitRepository(workspacePath))
                {
                    return commits;
                }
                
                using var repo = new Repository(workspacePath);
                
                foreach (var commit in repo.Commits.Take(maxCount))
                {
                    commits.Add(new GitCommit
                    {
                        Hash = commit.Sha,
                        ShortHash = commit.Sha.Substring(0, 7),
                        Author = commit.Author.Name,
                        AuthorEmail = commit.Author.Email,
                        CommitDate = commit.Author.When.DateTime,
                        Message = commit.MessageShort,
                        ParentHashes = commit.Parents.Select(p => p.Sha).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取所有提交历史失败: {ex.Message}");
            }
            
            return commits;
        });
    }
    
    /// <summary>
    /// 克隆远程仓库
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> CloneAsync(
        string gitUrl, 
        string localPath, 
        string branch, 
        GitCredentials? credentials = null,
        Action<CloneProgress>? progress = null)
    {
        return await Task.Run(() =>
        {
            string? tempSshKeyPath = null;
            string? tempAskPassPath = null;
            
            try
            {
                _logger?.LogInformation("开始克隆仓库: {GitUrl} -> {LocalPath}, 分支: {Branch}", gitUrl, localPath, branch);
                
                // 确保目标目录存在
                if (Directory.Exists(localPath))
                {
                    // 如果目录已存在且不为空，删除它
                    Directory.Delete(localPath, true);
                }
                Directory.CreateDirectory(localPath);
                
                var cloneOptions = new CloneOptions
                {
                    BranchName = branch,
                    Checkout = true
                };
                
                // 设置凭据提供器
                if (credentials != null && credentials.AuthType != "none")
                {
                    if (credentials.AuthType == "ssh" && !string.IsNullOrEmpty(credentials.SshPrivateKey))
                    {
                        // 使用 git CLI + 自定义 SSH 私钥（适用于容器环境）
                        tempSshKeyPath = CreateTempSshKeyFile(credentials.SshPrivateKey);
                        tempAskPassPath = CreateTempAskPassScript(credentials.SshPassphrase);
                        
                        var workingDir = Path.GetDirectoryName(localPath) ?? Directory.GetCurrentDirectory();
                        var result = ExecuteGitCommand(
                            workingDir,
                            $"clone --branch {branch} --single-branch \"{gitUrl}\" \"{localPath}\"",
                            tempSshKeyPath,
                            tempAskPassPath);
                        
                        if (result.ExitCode != 0)
                        {
                            var errorMessage = $"Git 操作失败: {result.StdErr}";
                            _logger?.LogError("克隆仓库失败: {GitUrl}, {Error}", gitUrl, result.StdErr);
                            
                            try
                            {
                                if (Directory.Exists(localPath))
                                {
                                    Directory.Delete(localPath, true);
                                }
                            }
                            catch { }
                            
                            return (false, errorMessage);
                        }
                        
                        _logger?.LogInformation("仓库克隆成功(SSH CLI): {LocalPath}", localPath);
                        return (true, null);
                    }
                    else
                    {
                        var credentialsHandler = CreateCredentialsHandler(credentials, tempSshKeyPath);
                        cloneOptions.FetchOptions.CredentialsProvider = credentialsHandler;
                    }
                }
                
                // 设置进度回调
                if (progress != null)
                {
                    cloneOptions.OnCheckoutProgress = (path, completedSteps, totalSteps) =>
                    {
                        var percentage = totalSteps > 0 ? (int)((double)completedSteps / totalSteps * 100) : 0;
                        progress(new CloneProgress
                        {
                            Percentage = percentage,
                            Stage = "检出文件",
                            Details = path
                        });
                    };
                }
                
                Repository.Clone(gitUrl, localPath, cloneOptions);
                
                _logger?.LogInformation("仓库克隆成功: {LocalPath}", localPath);
                return (true, null);
            }
            catch (LibGit2SharpException ex)
            {
                var errorMessage = $"Git 操作失败: {ex.Message}";
                _logger?.LogError(ex, "克隆仓库失败: {GitUrl}", gitUrl);
                
                // 清理失败的克隆目录
                try
                {
                    if (Directory.Exists(localPath))
                    {
                        Directory.Delete(localPath, true);
                    }
                }
                catch { }
                
                return (false, errorMessage);
            }
            catch (Exception ex)
            {
                var errorMessage = $"克隆失败: {ex.Message}";
                _logger?.LogError(ex, "克隆仓库失败: {GitUrl}", gitUrl);
                
                // 清理失败的克隆目录
                try
                {
                    if (Directory.Exists(localPath))
                    {
                        Directory.Delete(localPath, true);
                    }
                }
                catch { }
                
                return (false, errorMessage);
            }
            finally
            {
                // 清理临时SSH密钥文件
                CleanupTempSshKeyFile(tempSshKeyPath);
                CleanupTempAskPassScript(tempAskPassPath);
            }
        });
    }
    
    /// <summary>
    /// 拉取远程更新
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> PullAsync(
        string localPath, 
        GitCredentials? credentials = null)
    {
        return await Task.Run(() =>
        {
            string? tempSshKeyPath = null;
            string? tempAskPassPath = null;
            
            try
            {
                if (!IsGitRepository(localPath))
                {
                    return (false, "指定路径不是有效的 Git 仓库");
                }
                
                _logger?.LogInformation("开始拉取更新: {LocalPath}", localPath);
                
                using var repo = new Repository(localPath);
                
                var options = new PullOptions
                {
                    FetchOptions = new FetchOptions()
                };
                
                // 设置凭据提供器
                if (credentials != null && credentials.AuthType != "none")
                {
                    if (credentials.AuthType == "ssh" && !string.IsNullOrEmpty(credentials.SshPrivateKey))
                    {
                        // 使用 git CLI + 自定义 SSH 私钥（适用于容器环境）
                        tempSshKeyPath = CreateTempSshKeyFile(credentials.SshPrivateKey);
                        tempAskPassPath = CreateTempAskPassScript(credentials.SshPassphrase);
                        
                        var result = ExecuteGitCommand(
                            localPath,
                            "pull",
                            tempSshKeyPath,
                            tempAskPassPath);
                        
                        if (result.ExitCode != 0)
                        {
                            var errorMessage = $"Git 操作失败: {result.StdErr}";
                            _logger?.LogError("拉取更新失败: {LocalPath}, {Error}", localPath, result.StdErr);
                            return (false, errorMessage);
                        }
                        
                        _logger?.LogInformation("拉取更新成功(SSH CLI): {LocalPath}", localPath);
                        return (true, null);
                    }
                    else
                    {
                        options.FetchOptions.CredentialsProvider = CreateCredentialsHandler(credentials, tempSshKeyPath);
                    }
                }
                
                // 获取签名信息
                var signature = new Signature(
                    new Identity("WebCode", "webcode@local"), 
                    DateTimeOffset.Now);
                
                // 执行拉取
                Commands.Pull(repo, signature, options);
                
                _logger?.LogInformation("拉取更新成功: {LocalPath}", localPath);
                return (true, null);
            }
            catch (LibGit2SharpException ex)
            {
                var errorMessage = $"Git 操作失败: {ex.Message}";
                _logger?.LogError(ex, "拉取更新失败: {LocalPath}", localPath);
                return (false, errorMessage);
            }
            catch (Exception ex)
            {
                var errorMessage = $"拉取失败: {ex.Message}";
                _logger?.LogError(ex, "拉取更新失败: {LocalPath}", localPath);
                return (false, errorMessage);
            }
            finally
            {
                // 清理临时SSH密钥文件
                CleanupTempSshKeyFile(tempSshKeyPath);
                CleanupTempAskPassScript(tempAskPassPath);
            }
        });
    }
    
    /// <summary>
    /// 获取远程仓库的分支列表
    /// </summary>
    public async Task<(List<string> Branches, string? ErrorMessage)> ListRemoteBranchesAsync(
        string gitUrl, 
        GitCredentials? credentials = null)
    {
        return await Task.Run(() =>
        {
            var branches = new List<string>();
            string? tempSshKeyPath = null;
            string? tempAskPassPath = null;
            
            try
            {
                _logger?.LogInformation("获取远程分支列表: {GitUrl}", gitUrl);
                
                if (credentials?.AuthType == "https" && !string.IsNullOrEmpty(credentials.HttpsToken))
                {
                    var authUrl = BuildHttpsUrlWithToken(gitUrl, credentials.HttpsUsername, credentials.HttpsToken);
                    var result = ExecuteGitCommand(
                        Directory.GetCurrentDirectory(),
                        $"ls-remote --heads \"{authUrl}\"",
                        null,
                        null);

                    if (result.ExitCode != 0)
                    {
                        var errorMessage = $"获取分支列表失败: {result.StdErr}";
                        _logger?.LogError("获取远程分支列表失败(HTTPS CLI): {GitUrl}, {Error}", gitUrl, result.StdErr);
                        return (branches, errorMessage);
                    }

                    var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var refName = parts[^1];
                            if (refName.StartsWith("refs/heads/"))
                            {
                                branches.Add(refName.Replace("refs/heads/", ""));
                            }
                        }
                    }

                    _logger?.LogInformation("获取到 {Count} 个分支(HTTPS CLI)", branches.Count);
                    return (branches, null);
                }

                if (credentials?.AuthType == "ssh" && !string.IsNullOrEmpty(credentials.SshPrivateKey))
                {
                    tempSshKeyPath = CreateTempSshKeyFile(credentials.SshPrivateKey);
                    tempAskPassPath = CreateTempAskPassScript(credentials.SshPassphrase);
                    
                    var result = ExecuteGitCommand(
                        Directory.GetCurrentDirectory(),
                        $"ls-remote --heads \"{gitUrl}\"",
                        tempSshKeyPath,
                        tempAskPassPath);
                    
                    if (result.ExitCode != 0)
                    {
                        var errorMessage = $"获取分支列表失败: {result.StdErr}";
                        _logger?.LogError("获取远程分支列表失败: {GitUrl}, {Error}", gitUrl, result.StdErr);
                        return (branches, errorMessage);
                    }
                    
                    var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var refName = parts[^1];
                            if (refName.StartsWith("refs/heads/"))
                            {
                                branches.Add(refName.Replace("refs/heads/", ""));
                            }
                        }
                    }
                    
                    _logger?.LogInformation("获取到 {Count} 个分支(SSH CLI)", branches.Count);
                    return (branches, null);
                }
                
                // 创建临时目录用于 ls-remote
                var tempPath = Path.Combine(Path.GetTempPath(), $"git-ls-remote-{Guid.NewGuid():N}");
                var sshKeyPath = tempSshKeyPath; // 捕获到本地变量供闭包使用
                var sshPassphrase = credentials?.SshPassphrase;
                
                try
                {
                    // 使用 Repository.ListRemoteReferences 获取远程引用
                    var remoteRefs = Repository.ListRemoteReferences(gitUrl, (url, usernameFromUrl, types) =>
                    {
                        if (credentials == null || credentials.AuthType == "none")
                        {
                            return new DefaultCredentials();
                        }
                        
                        if (credentials.AuthType == "https" && !string.IsNullOrEmpty(credentials.HttpsToken))
                        {
                            return new UsernamePasswordCredentials
                            {
                                Username = string.IsNullOrWhiteSpace(credentials.HttpsUsername)
                                    ? "x-access-token"
                                    : credentials.HttpsUsername,
                                Password = credentials.HttpsToken
                            };
                        }
                        
                        if (credentials.AuthType == "ssh" && !string.IsNullOrEmpty(sshKeyPath))
                        {
                            // LibGit2Sharp 不支持直接使用SSH密钥文件
                            // 使用DefaultCredentials依赖系统SSH agent
                            _logger?.LogInformation("SSH 认证使用系统 SSH agent");
                            return new DefaultCredentials();
                        }
                        
                        return new DefaultCredentials();
                    });
                    
                    foreach (var reference in remoteRefs)
                    {
                        // 过滤分支引用 (refs/heads/)
                        if (reference.CanonicalName.StartsWith("refs/heads/"))
                        {
                            var branchName = reference.CanonicalName.Replace("refs/heads/", "");
                            branches.Add(branchName);
                        }
                    }
                    
                    _logger?.LogInformation("获取到 {Count} 个分支", branches.Count);
                }
                finally
                {
                    // 清理临时目录
                    try
                    {
                        if (Directory.Exists(tempPath))
                        {
                            Directory.Delete(tempPath, true);
                        }
                    }
                    catch { }
                }
                
                return (branches, null);
            }
            catch (LibGit2SharpException ex)
            {
                var errorMessage = $"获取分支列表失败: {ex.Message}";
                _logger?.LogError(ex, "获取远程分支列表失败: {GitUrl}", gitUrl);
                return (branches, errorMessage);
            }
            catch (Exception ex)
            {
                var errorMessage = $"获取分支列表失败: {ex.Message}";
                _logger?.LogError(ex, "获取远程分支列表失败: {GitUrl}", gitUrl);
                return (branches, errorMessage);
            }
            finally
            {
                // 清理临时SSH密钥文件
                CleanupTempSshKeyFile(tempSshKeyPath);
                CleanupTempAskPassScript(tempAskPassPath);
            }
        });
    }
    
    /// <summary>
    /// 获取本地仓库的当前分支
    /// </summary>
    public string? GetCurrentBranch(string localPath)
    {
        try
        {
            if (!IsGitRepository(localPath))
            {
                return null;
            }
            
            using var repo = new Repository(localPath);
            return repo.Head?.FriendlyName;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "获取当前分支失败: {LocalPath}", localPath);
            return null;
        }
    }
    
    /// <summary>
    /// 创建凭据处理器
    /// </summary>
    /// <param name="credentials">Git凭据</param>
    /// <param name="tempSshKeyPath">临时SSH密钥文件路径（需要调用者管理生命周期）</param>
    private CredentialsHandler CreateCredentialsHandler(GitCredentials credentials, string? tempSshKeyPath = null)
    {
        return (url, usernameFromUrl, types) =>
        {
            if (credentials.AuthType == "https" && !string.IsNullOrEmpty(credentials.HttpsToken))
            {
                return new UsernamePasswordCredentials
                {
                    Username = string.IsNullOrWhiteSpace(credentials.HttpsUsername)
                        ? "x-access-token"
                        : credentials.HttpsUsername,
                    Password = credentials.HttpsToken
                };
            }
            
            if (credentials.AuthType == "ssh" && !string.IsNullOrEmpty(tempSshKeyPath))
            {
                // LibGit2Sharp 不支持直接使用SSH密钥文件
                // 使用DefaultCredentials依赖系统SSH agent
                _logger?.LogInformation("SSH 认证使用系统 SSH agent");
                return new DefaultCredentials();
            }
            
            return new DefaultCredentials();
        };
    }

    private (int ExitCode, string StdOut, string StdErr) ExecuteGitCommand(
        string workingDirectory,
        string arguments,
        string? sshKeyPath,
        string? sshAskPassPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            if (!string.IsNullOrEmpty(sshKeyPath))
            {
                startInfo.Environment["GIT_SSH_COMMAND"] = BuildGitSshCommand(sshKeyPath);
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
                startInfo.Environment["GIT_SSH_VARIANT"] = "ssh";
            }
            else
            {
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            }
            
            if (!string.IsNullOrEmpty(sshAskPassPath))
            {
                startInfo.Environment["SSH_ASKPASS"] = sshAskPassPath;
                startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                startInfo.Environment["DISPLAY"] = "1";
            }
            
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (-1, string.Empty, "无法启动 git 进程");
            }
            
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private string BuildHttpsUrlWithToken(string gitUrl, string? username, string token)
    {
        try
        {
            var uri = new Uri(gitUrl);
            var user = string.IsNullOrWhiteSpace(username) ? "x-access-token" : username;
            var escapedUser = Uri.EscapeDataString(user);
            var escapedToken = Uri.EscapeDataString(token);
            var builder = new UriBuilder(uri)
            {
                UserName = escapedUser,
                Password = escapedToken
            };
            return builder.Uri.ToString();
        }
        catch
        {
            // 回退：简单拼接（避免异常阻断）
            var safeUser = string.IsNullOrWhiteSpace(username) ? "x-access-token" : username;
            return gitUrl.Replace("https://", $"https://{safeUser}:{token}@");
        }
    }

    private string BuildGitSshCommand(string sshKeyPath)
    {
        var escapedPath = sshKeyPath.Replace("\"", "\\\"");
        return $"ssh -i \"{escapedPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null";
    }

    private string? CreateTempAskPassScript(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return null;
        }
        
        var tempDir = Path.Combine(Path.GetTempPath(), "webcode-ssh-askpass");
        Directory.CreateDirectory(tempDir);
        
        var fileName = OperatingSystem.IsWindows()
            ? $"askpass_{Guid.NewGuid():N}.cmd"
            : $"askpass_{Guid.NewGuid():N}.sh";
        var tempFilePath = Path.Combine(tempDir, fileName);
        
        if (OperatingSystem.IsWindows())
        {
            var value = passphrase.Replace("%", "%%");
            File.WriteAllText(tempFilePath, $"@echo off{Environment.NewLine}set \"P={value}\"{Environment.NewLine}echo %P%{Environment.NewLine}");
        }
        else
        {
            var escaped = passphrase.Replace("'", "'\"'\"'");
            File.WriteAllText(tempFilePath, $"#!/bin/sh\nprintf '%s' '{escaped}'\n");
            try
            {
                File.SetUnixFileMode(tempFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { }
        }
        
        return tempFilePath;
    }

    private void CleanupTempAskPassScript(string? tempFilePath)
    {
        if (string.IsNullOrEmpty(tempFilePath))
        {
            return;
        }
        
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "清理临时SSH AskPass脚本失败: {Path}", tempFilePath);
        }
    }
    
    /// <summary>
    /// 创建临时SSH密钥文件
    /// </summary>
    /// <param name="privateKeyContent">SSH私钥内容</param>
    /// <returns>临时文件路径</returns>
    private string CreateTempSshKeyFile(string privateKeyContent)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "webcode-ssh-keys");
        Directory.CreateDirectory(tempDir);
        
        var tempFilePath = Path.Combine(tempDir, $"id_rsa_{Guid.NewGuid():N}");
        
        // 写入私钥内容，确保使用Unix换行符
        var normalizedContent = privateKeyContent.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(tempFilePath, normalizedContent);
        
        // 在Windows上设置文件权限（仅当前用户可读）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var fileInfo = new FileInfo(tempFilePath);
                var security = fileInfo.GetAccessControl();
                
                // 移除继承的权限
                security.SetAccessRuleProtection(true, false);
                
                // 清除所有现有规则
                var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount));
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
                {
                    security.RemoveAccessRule(rule);
                }
                
                // 只添加当前用户的完全控制权限
                var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    currentUser,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                
                fileInfo.SetAccessControl(security);
                _logger?.LogDebug("已设置SSH密钥文件权限: {Path}", tempFilePath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "设置SSH密钥文件权限失败，可能导致认证问题");
            }
        }
        else
        {
            // Unix/Linux/macOS: 使用chmod设置600权限
            try
            {
                File.SetUnixFileMode(tempFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "设置SSH密钥文件权限失败");
            }
        }
        
        _logger?.LogDebug("已创建临时SSH密钥文件: {Path}", tempFilePath);
        return tempFilePath;
    }
    
    /// <summary>
    /// 清理临时SSH密钥文件
    /// </summary>
    /// <param name="tempFilePath">临时文件路径</param>
    private void CleanupTempSshKeyFile(string? tempFilePath)
    {
        if (string.IsNullOrEmpty(tempFilePath))
        {
            return;
        }
        
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
                _logger?.LogDebug("已清理临时SSH密钥文件: {Path}", tempFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "清理临时SSH密钥文件失败: {Path}", tempFilePath);
        }
    }
}
