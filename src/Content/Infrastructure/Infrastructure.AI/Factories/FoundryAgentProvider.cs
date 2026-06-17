using Application.AI.Common.Interfaces;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Factories;

/// <summary>
/// Infrastructure implementation of <see cref="IFoundryAgentProvider"/> that builds a non-versioned
/// Azure AI Foundry Responses <see cref="AIAgent"/> (direct inference) via
/// <see cref="AIProjectClient"/>. Quarantines the Azure AI Projects SDK to the Infrastructure layer
/// so the Application layer (<c>AgentFactory</c>) stays SDK-light.
/// </summary>
/// <remarks>
/// Registered as a singleton only when <c>AppConfig.AI.AIFoundry.IsConfigured</c> is true (see
/// <c>RegisterAIFoundryAgents</c>). The harness middleware pipeline is supplied by the caller via
/// the <c>clientFactory</c> hook and applied to the underlying Responses
/// <see cref="IChatClient"/>, so the Foundry path retains the same observability, function
/// invocation, prerequisite gating, and caching behaviour as the other providers.
/// </remarks>
public sealed class FoundryAgentProvider : IFoundryAgentProvider
{
    private readonly AIProjectClient _projectClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FoundryAgentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FoundryAgentProvider"/> class.
    /// </summary>
    /// <param name="projectClient">The AI Foundry project client used for Responses API calls.</param>
    /// <param name="loggerFactory">Logger factory passed through to the constructed agent.</param>
    /// <param name="serviceProvider">
    /// Service provider used to resolve services required by tool (<see cref="AIFunction"/>)
    /// invocations during a turn.
    /// </param>
    public FoundryAgentProvider(
        AIProjectClient projectClient,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _projectClient = projectClient;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<FoundryAgentProvider>();
    }

    /// <inheritdoc />
    public Task<AIAgent> CreateAgentAsync(
        string model,
        ChatClientAgentOptions options,
        Func<IChatClient, IChatClient> clientFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientFactory);

        // The Responses API selects the deployment from ChatOptions.ModelId. Preserve any value the
        // caller already set; otherwise bind the harness-resolved model/deployment.
        options.ChatOptions ??= new ChatOptions();
        options.ChatOptions.ModelId ??= model;

        _logger.LogInformation(
            "Creating Foundry Responses agent {AgentName} for model {Model} " +
            "(direct inference, no server-managed resource)",
            options.Name, model);

        // Non-versioned (direct-inference) Responses agent. The harness middleware pipeline is
        // injected through clientFactory; serviceProvider resolves tool dependencies at invocation.
        var agent = _projectClient.AsAIAgent(options, clientFactory, _loggerFactory, _serviceProvider);

        return Task.FromResult<AIAgent>(agent);
    }
}
