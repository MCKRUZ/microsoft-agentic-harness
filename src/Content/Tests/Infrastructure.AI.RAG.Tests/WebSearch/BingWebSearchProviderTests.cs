using System.Net;
using System.Text.Json;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.WebSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.WebSearch;

public sealed class BingWebSearchProviderTests
{
    [Fact]
    public async Task SearchAsync_ParsesBingApiResponse_ReturnsStructuredResults()
    {
        var bingResponse = new
        {
            webPages = new
            {
                value = new[]
                {
                    new { name = "Result 1", snippet = "Snippet 1", url = "https://example.com/1" },
                    new { name = "Result 2", snippet = "Snippet 2", url = "https://example.com/2" }
                }
            }
        };
        var json = JsonSerializer.Serialize(bingResponse);
        var handler = new FakeHttpMessageHandler(json, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.bing.microsoft.com/") };

        var config = CreateConfig();
        var sut = new BingWebSearchProvider(httpClient, config, NullLogger<BingWebSearchProvider>.Instance);

        var results = await sut.SearchAsync("test query", 5, CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Title.Should().Be("Result 1");
        results[0].Snippet.Should().Be("Snippet 1");
        results[0].Url.Should().Be("https://example.com/1");
    }

    [Fact]
    public async Task SearchAsync_EmptyResponse_ReturnsEmptyList()
    {
        var bingResponse = new { webPages = new { value = Array.Empty<object>() } };
        var json = JsonSerializer.Serialize(bingResponse);
        var handler = new FakeHttpMessageHandler(json, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.bing.microsoft.com/") };

        var config = CreateConfig();
        var sut = new BingWebSearchProvider(httpClient, config, NullLogger<BingWebSearchProvider>.Instance);

        var results = await sut.SearchAsync("test query", 5, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ApiError_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler("error", HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.bing.microsoft.com/") };

        var config = CreateConfig();
        var sut = new BingWebSearchProvider(httpClient, config, NullLogger<BingWebSearchProvider>.Instance);

        var results = await sut.SearchAsync("test query", 5, CancellationToken.None);

        results.Should().BeEmpty();
    }

    private static IOptionsMonitor<AppConfig> CreateConfig()
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.WebSearch = new WebSearchConfig
        {
            Provider = "bing",
            Market = "en-US",
            SafeSearch = "Moderate"
        };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }

    private sealed class FakeHttpMessageHandler(string responseContent, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
