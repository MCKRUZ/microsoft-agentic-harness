using System.Net;
using Domain.AI.A2A;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.A2A;
using FluentAssertions;
using Infrastructure.AI.A2A;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.AI.Tests.A2A;

/// <summary>
/// Tests for <see cref="A2AAgentHost"/> covering agent card generation,
/// remote agent discovery, and task delegation over HTTP.
/// </summary>
public sealed class A2AAgentHostTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactory = new();
    private readonly A2AAgentHost _sut;
    private readonly A2AConfig _a2aConfig;

    public A2AAgentHostTests()
    {
        _a2aConfig = new A2AConfig
        {
            AgentName = "TestAgent",
            AgentDescription = "A test agent",
            BaseUrl = "https://localhost:5001"
        };

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                A2A = _a2aConfig
            }
        };

        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _sut = new A2AAgentHost(
            options,
            _httpClientFactory.Object,
            NullLogger<A2AAgentHost>.Instance);
    }

    [Fact]
    public void GetAgentCard_ReturnsConfiguredValues()
    {
        var card = _sut.GetAgentCard();

        card.Name.Should().Be("TestAgent");
        card.Description.Should().Be("A test agent");
        card.Url.Should().Be("https://localhost:5001");
    }

    [Fact]
    public async Task DiscoverAgentsAsync_NoRemoteAgents_ReturnsEmptyList()
    {
        _a2aConfig.RemoteAgents = new List<RemoteAgentEndpoint>();

        var result = await _sut.DiscoverAgentsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAgentsAsync_SuccessfulDiscovery_ReturnsAgentCards()
    {
        _a2aConfig.RemoteAgents = new List<RemoteAgentEndpoint>
        {
            new() { Name = "Remote1", Url = "https://remote1.example.com" }
        };

        var agentCardJson = """{"name":"Remote1","description":"Remote agent","url":"https://remote1.example.com"}""";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, agentCardJson);
        _httpClientFactory.Setup(f => f.CreateClient("A2A")).Returns(httpClient);

        var result = await _sut.DiscoverAgentsAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Remote1");
    }

    [Fact]
    public async Task DiscoverAgentsAsync_FailedDiscovery_ReturnsEmptyList()
    {
        _a2aConfig.RemoteAgents = new List<RemoteAgentEndpoint>
        {
            new() { Name = "Unreachable", Url = "https://unreachable.example.com" }
        };

        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "");
        _httpClientFactory.Setup(f => f.CreateClient("A2A")).Returns(httpClient);

        var result = await _sut.DiscoverAgentsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SendTaskAsync_PostsToTasksEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"taskId":"t-1"}""")
            });

        var httpClient = new HttpClient(handler.Object);
        _httpClientFactory.Setup(f => f.CreateClient("A2A")).Returns(httpClient);

        var result = await _sut.SendTaskAsync(
            "https://remote.example.com/agent",
            "Analyze this code");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Post);
        capturedRequest.RequestUri!.ToString().Should().Be("https://remote.example.com/agent/tasks");
        result.Should().Contain("t-1");
    }

    [Fact]
    public async Task DiscoverAgentsAsync_WithApiKey_SetsAuthorizationHeader()
    {
        _a2aConfig.RemoteAgents = new List<RemoteAgentEndpoint>
        {
            new() { Name = "Secured", Url = "https://secured.example.com", ApiKey = "secret-key" }
        };

        HttpRequestMessage? capturedRequest = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"Secured","description":"desc","url":"https://secured.example.com"}""")
            });

        var httpClient = new HttpClient(handler.Object);
        _httpClientFactory.Setup(f => f.CreateClient("A2A")).Returns(httpClient);

        await _sut.DiscoverAgentsAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.GetValues("Authorization").Should().Contain("Bearer secret-key");
    }

    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string content)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });

        return new HttpClient(handler.Object);
    }
}
