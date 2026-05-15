using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(ISherpaKokoroTtsClient), ServiceLifetime.Scoped)]
public sealed class SherpaKokoroTtsClient : ISherpaKokoroTtsClient
{
    private const string HttpClientName = "SherpaKokoroTtsClient";

    private readonly FeishuReplyTtsOptions _options;
    private readonly ILogger<SherpaKokoroTtsClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public SherpaKokoroTtsClient(
        IOptions<FeishuReplyTtsOptions> options,
        ILogger<SherpaKokoroTtsClient> logger,
        IHttpClientFactory httpClientFactory)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClientFactory?.CreateClient(HttpClientName) ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _baseUri = CreateBaseUri(_options.TtsServiceBaseUrl);
    }

    public async Task<FeishuReplyTtsHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/health"));
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseResponseAsync(response, cancellationToken);

        var root = document.RootElement;
        var status = GetString(root, "status");
        var isAvailable = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);

        return new FeishuReplyTtsHealthStatus
        {
            IsAvailable = isAvailable,
            Message = isAvailable
                ? "Local Kokoro/sherpa-onnx service is healthy."
                : $"Local Kokoro/sherpa-onnx service reported status '{status ?? "unknown"}'.",
            ServiceStatus = status,
            Device = GetString(root, "device"),
            DefaultVoiceId = GetString(root, "defaultVoiceId", "default_voice_id")
        };
    }

    public async Task<IReadOnlyList<FeishuReplyTtsVoiceOption>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/voices"));
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseResponseAsync(response, cancellationToken);

        var voicesElement = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement,
            _ when document.RootElement.TryGetProperty("voices", out var arrayElement) => arrayElement,
            _ => default
        };

        if (voicesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var voices = new List<FeishuReplyTtsVoiceOption>();
        foreach (var item in voicesElement.EnumerateArray())
        {
            var voiceId = GetString(item, "voiceId", "voice_id");
            if (string.IsNullOrWhiteSpace(voiceId))
            {
                continue;
            }

            voices.Add(new FeishuReplyTtsVoiceOption
            {
                VoiceId = voiceId,
                DisplayName = GetString(item, "displayName", "display_name", "name") ?? voiceId,
                Language = GetString(item, "language"),
                Gender = GetString(item, "gender")
            });
        }

        return voices;
    }

    public async Task<Stream> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }

        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("Voice ID is required.", nameof(voiceId));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/synthesize"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    text,
                    voice_id = voiceId
                }),
                Encoding.UTF8,
                "application/json")
        };

        _logger.LogInformation(
            "Starting Kokoro/sherpa-onnx synthesis. VoiceId={VoiceId}, TextLength={TextLength}",
            voiceId,
            text.Length);

        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Kokoro/sherpa-onnx synthesize request failed: Status={StatusCode}, Content={Content}", response.StatusCode, error);
            throw new HttpRequestException($"Kokoro/sherpa-onnx synthesize request failed: {response.StatusCode}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var output = new MemoryStream();
        await source.CopyToAsync(output, cancellationToken);
        output.Position = 0;
        _logger.LogInformation(
            "Completed Kokoro/sherpa-onnx synthesis. VoiceId={VoiceId}, TextLength={TextLength}, WaveBytes={WaveBytes}",
            voiceId,
            text.Length,
            output.Length);
        return output;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.TtsServiceTimeoutSeconds <= 0)
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TtsServiceTimeoutSeconds));
        try
        {
            return await _httpClient.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Kokoro/sherpa-onnx request timed out after {TimeoutSeconds}s. Method={Method}, Url={Url}",
                _options.TtsServiceTimeoutSeconds,
                request.Method,
                request.RequestUri);
            throw new TimeoutException(
                $"Kokoro/sherpa-onnx request timed out after {_options.TtsServiceTimeoutSeconds} seconds.",
                ex);
        }
    }

    private static async Task<JsonDocument> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Kokoro/sherpa-onnx request failed: {response.StatusCode}");
        }

        return JsonDocument.Parse(content);
    }

    private Uri BuildUri(string relativePath)
    {
        return new Uri(_baseUri, relativePath.TrimStart('/'));
    }

    private static Uri CreateBaseUri(string? baseUrl)
    {
        var candidate = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://127.0.0.1:5058/"
            : baseUrl.Trim();

        if (!candidate.EndsWith("/", StringComparison.Ordinal))
        {
            candidate += "/";
        }

        return new Uri(candidate, UriKind.Absolute);
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }
}
