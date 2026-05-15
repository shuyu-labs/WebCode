using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class SherpaKokoroTtsClientTests
{
    [Fact]
    public async Task GetHealthAsync_ParsesLocalServiceResponse()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"status":"ok","device":"cpu","defaultVoiceId":"zh_female_1"}""")
        ]);

        var client = CreateClient(handler);

        var result = await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal("ok", result.ServiceStatus);
        Assert.Equal("cpu", result.Device);
        Assert.Equal("zh_female_1", result.DefaultVoiceId);
        Assert.Equal("/health", Assert.Single(handler.RequestPaths));
    }

    [Fact]
    public async Task GetVoicesAsync_ParsesVoiceList()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse(
                """
                {
                  "voices": [
                    {
                      "voiceId": "zh_female_1",
                      "displayName": "Kokoro Chinese Female",
                      "language": "zh",
                      "gender": "female"
                    },
                    {
                      "voiceId": "en_male_1",
                      "displayName": "Kokoro English Male",
                      "language": "en",
                      "gender": "male"
                    }
                  ]
                }
                """)
        ]);

        var client = CreateClient(handler);

        var result = await client.GetVoicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Collection(
            result,
            voice =>
            {
                Assert.Equal("zh_female_1", voice.VoiceId);
                Assert.Equal("Kokoro Chinese Female", voice.DisplayName);
                Assert.Equal("zh", voice.Language);
                Assert.Equal("female", voice.Gender);
            },
            voice =>
            {
                Assert.Equal("en_male_1", voice.VoiceId);
                Assert.Equal("Kokoro English Male", voice.DisplayName);
                Assert.Equal("en", voice.Language);
                Assert.Equal("male", voice.Gender);
            });
        Assert.Equal("/voices", Assert.Single(handler.RequestPaths));
    }

    [Fact]
    public async Task GetHealthAsync_UsesDedicatedHttpClientName()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"status":"ok"}""")
        ]);
        var factory = new StubHttpClientFactory(new HttpClient(handler));
        var client = CreateClient(handler, factory);

        await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal("SherpaKokoroTtsClient", factory.CreatedClientName);
    }

    private static SherpaKokoroTtsClient CreateClient(StubHttpMessageHandler handler, StubHttpClientFactory? factory = null)
    {
        return new SherpaKokoroTtsClient(
            Options.Create(new FeishuReplyTtsOptions
            {
                TtsServiceBaseUrl = "http://127.0.0.1:5058",
                TtsServiceTimeoutSeconds = 15
            }),
            NullLogger<SherpaKokoroTtsClient>.Instance,
            factory ?? new StubHttpClientFactory(new HttpClient(handler)));
    }

    private static HttpResponseMessage CreateJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public string? CreatedClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreatedClientName = name;
            return client;
        }
    }

    private sealed class StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);

            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
