using System.Text.Json;
using Application.AI.Common.Interfaces.A2A;
using Domain.AI.A2A;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Infrastructure implementation of <see cref="IA2AAgentHost"/> providing
/// agent card publishing, remote agent discovery via well-known endpoints,
/// and task delegation over HTTP.
/// </summary>
public class A2AAgentHost : IA2AAgentHost
{
    private readonly IOptionsMonitor<AppConfig> _appConfigMonitor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<A2AAgentHost> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of <see cref="A2AAgentHost"/>.
    /// </summary>
    /// <param name="appConfigMonitor">Options monitor providing current <see cref="AppConfig"/>.</param>
    /// <param name="httpClientFactory">Factory for creating named HTTP clients.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public A2AAgentHost(
        IOptionsMonitor<AppConfig> appConfigMonitor,
        IHttpClientFactory httpClientFactory,
        ILogger<A2AAgentHost> logger)
    {
        _appConfigMonitor = appConfigMonitor;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentCard GetAgentCard()
    {
        var a2aConfig = _appConfigMonitor.CurrentValue.AI.A2A;

        return new AgentCard
        {
            Name = a2aConfig.AgentName,
            Description = a2aConfig.AgentDescription,
            Url = a2aConfig.BaseUrl
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentCard>> DiscoverAgentsAsync(CancellationToken cancellationToken = default)
    {
        var a2aConfig = _appConfigMonitor.CurrentValue.AI.A2A;
        var discovered = new List<AgentCard>();
        var client = _httpClientFactory.CreateClient("A2A");

        foreach (var remote in a2aConfig.RemoteAgents)
        {
            try
            {
                var wellKnownUrl = $"{remote.Url.TrimEnd('/')}/.well-known/agent.json";
                using var request = new HttpRequestMessage(HttpMethod.Get, wellKnownUrl);

                if (!string.IsNullOrEmpty(remote.ApiKey))
                {
                    request.Headers.Add("Authorization", $"Bearer {remote.ApiKey}");
                }

                using var response = await client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var card = JsonSerializer.Deserialize<AgentCard>(json, _jsonOptions);

                if (card is not null)
                {
                    discovered.Add(card);
                    _logger.LogInformation("Discovered A2A agent '{AgentName}' at {Url}", card.Name, remote.Url);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover A2A agent '{AgentName}' at {Url}", remote.Name, remote.Url);
            }
        }

        return discovered;
    }

    /// <inheritdoc />
    public async Task<string> SendTaskAsync(string agentUrl, string taskDescription, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("A2A");
        var tasksUrl = $"{agentUrl.TrimEnd('/')}/tasks";

        var payload = JsonSerializer.Serialize(new { description = taskDescription }, _jsonOptions);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(tasksUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("Sent A2A task to {Url}, received {Length} byte response", tasksUrl, responseBody.Length);

        return responseBody;
    }
}
