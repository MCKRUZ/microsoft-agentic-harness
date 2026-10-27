using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Scoping;

/// <summary>
/// Says, once at startup outside Development, what users share when knowledge isolation is off
/// (<c>AppConfig:AI:Rag:GraphRag:MultiTenantIsolation</c> is <see langword="false"/>, the default) (#904).
/// </summary>
/// <remarks>
/// <para>
/// Off is a reasonable default for a single-tenant deployment, and a poor one to leave unnoticed in a deployment several
/// people or organisations use. It reads the setting from the same <see cref="IOptionsMonitor{TOptions}"/> the store's
/// registration reads, so the warning and the store cannot disagree about whether isolation is on.
/// </para>
/// <para>
/// The message claims only what the code does. Each user's remembered facts are private either way: their ids are
/// namespaced by user and tenant. With isolation off, the knowledge graph's entities and the learnings recalled into
/// turns are visible to every user and tenant. Ingested documents are searchable by every user whether isolation is on
/// or off, so the message does not suggest that turning it on makes documents private.
/// </para>
/// </remarks>
public sealed class KnowledgeIsolationOffStartupWarning : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<KnowledgeIsolationOffStartupWarning> _logger;
    private readonly IHostEnvironment? _environment;

    /// <summary>Initializes a new instance of the <see cref="KnowledgeIsolationOffStartupWarning"/> class.</summary>
    /// <param name="config">The application configuration the knowledge-graph store is built from.</param>
    /// <param name="logger">Logger the warning is written to.</param>
    /// <param name="environment">
    /// The environment the host runs in. A composition with no host environment is treated as not Development, so it
    /// warns, as the content-safety and egress startup checks do.
    /// </param>
    public KnowledgeIsolationOffStartupWarning(
        IOptionsMonitor<AppConfig> config,
        ILogger<KnowledgeIsolationOffStartupWarning> logger,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.CurrentValue.AI.Rag.GraphRag.MultiTenantIsolation && !(_environment?.IsDevelopment() ?? false))
            _logger.LogWarning(
                "Knowledge isolation is off (AppConfig:AI:Rag:GraphRag:MultiTenantIsolation is false). Every user and " +
                "tenant shares the knowledge graph's entities and the learnings recalled into turns. Each user's " +
                "remembered facts stay private, and ingested documents are searchable by every user either way. Turn " +
                "isolation on when users or tenants must not see each other's learnings or graph entities.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
