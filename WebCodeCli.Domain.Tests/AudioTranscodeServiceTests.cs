using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class AudioTranscodeServiceTests : IDisposable
{
    private readonly string _sandboxRoot = Path.Combine(Path.GetTempPath(), "webcode-audio-transcode-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TranscodeChunkAsync_WhenFfmpegPathIsMissing_Throws()
    {
        Directory.CreateDirectory(_sandboxRoot);
        var service = CreateService(
            new FeishuReplyTtsOptions
            {
                TtsStorageRoot = Path.Combine(_sandboxRoot, "reply-tts")
            },
            new RecordingExternalProcessRunner());
        var inputPath = CreateInputFile();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranscodeChunkAsync("job-1", inputPath, chunkIndex: 1, TestContext.Current.CancellationToken));

        Assert.Contains("FfmpegExecutablePath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscodeChunkAsync_WhenTempRootIsUnavailable_Throws()
    {
        Directory.CreateDirectory(_sandboxRoot);
        var options = new FeishuReplyTtsOptions
        {
            FfmpegExecutablePath = Path.Combine(_sandboxRoot, "ffmpeg.exe")
        };
        var service = new AudioTranscodeService(
            Options.Create(options),
            new ReplyTtsStorageRootResolver(
                new MutableOptionsMonitor<FeishuReplyTtsOptions>(options),
                new FakeReplyTtsHostEnvironment(
                    isWindows: true,
                    systemDriveRoot: @"C:\",
                    drives:
                    [
                        new ReplyTtsDriveDescriptor(@"C:\", isReady: true, isWritable: true)
                    ])),
            new RecordingExternalProcessRunner());
        var inputPath = CreateInputFile();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranscodeChunkAsync("job-1", inputPath, chunkIndex: 1, TestContext.Current.CancellationToken));

        Assert.Contains("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscodeChunkAsync_WritesUnderResolvedTempRoot_AndInvokesExpectedFfmpegArguments()
    {
        Directory.CreateDirectory(_sandboxRoot);
        var runner = new RecordingExternalProcessRunner();
        var ffmpegPath = Path.Combine(_sandboxRoot, "ffmpeg.exe");
        File.WriteAllText(ffmpegPath, "stub");

        var service = CreateService(
            new FeishuReplyTtsOptions
            {
                TtsStorageRoot = Path.Combine(_sandboxRoot, "reply-tts"),
                FfmpegExecutablePath = ffmpegPath
            },
            runner);
        var inputPath = CreateInputFile();

        var outputPath = await service.TranscodeChunkAsync("job-42", inputPath, chunkIndex: 1, TestContext.Current.CancellationToken);

        Assert.Equal(ffmpegPath, runner.FileName);
        Assert.Contains("-y", runner.Arguments, StringComparison.Ordinal);
        Assert.Contains("-i", runner.Arguments, StringComparison.Ordinal);
        Assert.Contains("libopus", runner.Arguments, StringComparison.Ordinal);
        Assert.Contains("-ac 1", runner.Arguments, StringComparison.Ordinal);
        Assert.Contains("-ar 16000", runner.Arguments, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_sandboxRoot, "reply-tts", "temp", "job-42"), runner.WorkingDirectory);
        Assert.Equal(Path.Combine(_sandboxRoot, "reply-tts", "temp", "job-42", "chunk-001.opus"), outputPath);
    }

    [Fact]
    public async Task TranscodeChunkAsync_WhenRunnerReturnsNonZeroExit_Throws()
    {
        Directory.CreateDirectory(_sandboxRoot);
        var ffmpegPath = Path.Combine(_sandboxRoot, "ffmpeg.exe");
        File.WriteAllText(ffmpegPath, "stub");
        var service = CreateService(
            new FeishuReplyTtsOptions
            {
                TtsStorageRoot = Path.Combine(_sandboxRoot, "reply-tts"),
                FfmpegExecutablePath = ffmpegPath
            },
            new RecordingExternalProcessRunner
            {
                Result = new ExternalProcessResult(1, string.Empty, "ffmpeg failed")
            });
        var inputPath = CreateInputFile();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranscodeChunkAsync("job-42", inputPath, chunkIndex: 1, TestContext.Current.CancellationToken));

        Assert.Contains("exit code 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscodeChunkAsync_WhenConfiguredPathIsBlank_UsesBundledStorageRootFfmpeg()
    {
        Directory.CreateDirectory(_sandboxRoot);
        var runner = new RecordingExternalProcessRunner();
        var storageRoot = Path.Combine(_sandboxRoot, "reply-tts");
        var bundledFfmpegPath = Path.Combine(storageRoot, "ffmpeg", "bin", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(bundledFfmpegPath)!);
        File.WriteAllText(bundledFfmpegPath, "stub");

        var service = CreateService(
            new FeishuReplyTtsOptions
            {
                TtsStorageRoot = storageRoot
            },
            runner);
        var inputPath = CreateInputFile();

        await service.TranscodeChunkAsync("job-42", inputPath, chunkIndex: 1, TestContext.Current.CancellationToken);

        Assert.Equal(bundledFfmpegPath, runner.FileName);
    }

    [Fact]
    public async Task TranscodeChunkAsync_SanitizesJobIdToStayUnderTempRoot()
    {
        Directory.CreateDirectory(_sandboxRoot);
        var runner = new RecordingExternalProcessRunner();
        var ffmpegPath = Path.Combine(_sandboxRoot, "ffmpeg.exe");
        File.WriteAllText(ffmpegPath, "stub");
        var service = CreateService(
            new FeishuReplyTtsOptions
            {
                TtsStorageRoot = Path.Combine(_sandboxRoot, "reply-tts"),
                FfmpegExecutablePath = ffmpegPath
            },
            runner);
        var inputPath = CreateInputFile();

        var outputPath = await service.TranscodeChunkAsync("..", inputPath, chunkIndex: 1, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_sandboxRoot, "reply-tts", "temp", "__", "chunk-001.opus"), outputPath);
        Assert.StartsWith(Path.Combine(_sandboxRoot, "reply-tts", "temp"), outputPath, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_sandboxRoot, "reply-tts", "temp", "__"), runner.WorkingDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private AudioTranscodeService CreateService(FeishuReplyTtsOptions options, RecordingExternalProcessRunner runner)
    {
        return new AudioTranscodeService(
            Options.Create(options),
            new ReplyTtsStorageRootResolver(
                new MutableOptionsMonitor<FeishuReplyTtsOptions>(options),
                new FakeReplyTtsHostEnvironment(isWindows: false, systemDriveRoot: null, drives: [])),
            runner);
    }

    private string CreateInputFile()
    {
        var inputPath = Path.Combine(_sandboxRoot, "input.wav");
        File.WriteAllText(inputPath, "wav");
        return inputPath;
    }

    private sealed class RecordingExternalProcessRunner : IExternalProcessRunner
    {
        public ExternalProcessResult Result { get; set; } = new(0, string.Empty, string.Empty);

        public string? FileName { get; private set; }

        public string? Arguments { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(Result);
        }
    }

    private sealed class MutableOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; private set; } = currentValue;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private sealed class FakeReplyTtsHostEnvironment(
        bool isWindows,
        string? systemDriveRoot,
        IReadOnlyList<ReplyTtsDriveDescriptor> drives) : IReplyTtsHostEnvironment
    {
        public bool IsWindows { get; } = isWindows;

        public string? SystemDriveRoot { get; } = systemDriveRoot;

        public IReadOnlyList<ReplyTtsDriveDescriptor> GetFixedDrives() => drives;

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);
    }
}
