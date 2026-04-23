using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Resolves the active <see cref="IChunkingService"/> implementation based on
/// configuration or an explicit override. Reads <c>AppConfig:AI:Rag:Ingestion:DefaultStrategy</c>
/// and maps it to one of the keyed <see cref="IChunkingService"/> registrations
/// (<c>"structure_aware"</c>, <c>"fixed_size"</c>, <c>"semantic"</c>).
/// </summary>
public sealed class ChunkingStrategyResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<ChunkingStrategyResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkingStrategyResolver"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving keyed chunking services.</param>
    /// <param name="appConfig">Application configuration providing the default strategy.</param>
    /// <param name="logger">Logger for recording strategy resolution decisions.</param>
    public ChunkingStrategyResolver(
        IServiceProvider serviceProvider,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<ChunkingStrategyResolver> logger)
    {
        _serviceProvider = serviceProvider;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the <see cref="IChunkingService"/> for the given strategy, falling
    /// back to the configured default when no override is specified.
    /// </summary>
    /// <param name="overrideStrategy">
    /// An explicit strategy to use instead of the configured default.
    /// Pass <c>null</c> to use the default from <c>AppConfig:AI:Rag:Ingestion:DefaultStrategy</c>.
    /// </param>
    /// <returns>The resolved <see cref="IChunkingService"/> implementation.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no keyed service is registered for the resolved strategy key.
    /// </exception>
    public IChunkingService ResolveChunker(ChunkingStrategy? overrideStrategy = null)
    {
        var strategyKey = overrideStrategy.HasValue
            ? MapStrategyToKey(overrideStrategy.Value)
            : _appConfig.CurrentValue.AI.Rag.Ingestion.DefaultStrategy;

        _logger.LogDebug("Resolving chunking strategy: {Strategy}", strategyKey);

        var chunker = _serviceProvider.GetKeyedService<IChunkingService>(strategyKey);

        if (chunker is null)
        {
            throw new InvalidOperationException(
                $"No IChunkingService registered for strategy key '{strategyKey}'. " +
                "Available keys: structure_aware, fixed_size, semantic.");
        }

        return chunker;
    }

    /// <summary>
    /// Maps a <see cref="ChunkingStrategy"/> enum value to its keyed DI service name.
    /// </summary>
    private static string MapStrategyToKey(ChunkingStrategy strategy) => strategy switch
    {
        ChunkingStrategy.StructureAware => "structure_aware",
        ChunkingStrategy.Semantic => "semantic",
        ChunkingStrategy.FixedSize => "fixed_size",
        ChunkingStrategy.Hierarchical => "structure_aware", // Hierarchical reuses structure-aware base
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown chunking strategy.")
    };
}
