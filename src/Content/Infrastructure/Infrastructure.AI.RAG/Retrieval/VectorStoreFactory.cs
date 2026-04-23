using Application.AI.Common.Interfaces.RAG;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Factory for resolving keyed <see cref="IVectorStore"/> and <see cref="IBm25Store"/>
/// implementations based on the configured provider in <c>AppConfig:AI:Rag:VectorStore:Provider</c>.
/// Provides explicit resolution methods for scenarios where dynamic provider switching
/// is needed beyond what the default DI registration provides.
/// </summary>
/// <remarks>
/// <para>
/// Provider mapping:
/// <list type="bullet">
///   <item><c>"azure_ai_search"</c> maps to <see cref="AzureAISearchVectorStore"/> and
///         <see cref="AzureAISearchBm25Store"/>.</item>
///   <item><c>"faiss"</c> maps to <see cref="FaissVectorStore"/> and
///         <see cref="SqliteFts5Store"/>.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class VectorStoreFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<AppConfig> _appConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="VectorStoreFactory"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving keyed services.</param>
    /// <param name="appConfig">The application configuration monitor.</param>
    public VectorStoreFactory(
        IServiceProvider serviceProvider,
        IOptionsMonitor<AppConfig> appConfig)
    {
        _serviceProvider = serviceProvider;
        _appConfig = appConfig;
    }

    /// <summary>
    /// Resolves the <see cref="IVectorStore"/> implementation for the configured provider.
    /// </summary>
    /// <param name="providerOverride">
    /// Optional provider name to override the configured default. When null, uses
    /// <c>AppConfig:AI:Rag:VectorStore:Provider</c>.
    /// </param>
    /// <returns>The vector store implementation for the specified provider.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no keyed service is registered for the provider name.
    /// </exception>
    public IVectorStore ResolveVectorStore(string? providerOverride = null)
    {
        var provider = providerOverride
            ?? _appConfig.CurrentValue.AI.Rag.VectorStore.Provider;

        return _serviceProvider.GetRequiredKeyedService<IVectorStore>(provider);
    }

    /// <summary>
    /// Resolves the <see cref="IBm25Store"/> implementation for the configured provider.
    /// Azure AI Search uses its native BM25; local providers use SQLite FTS5.
    /// </summary>
    /// <param name="providerOverride">
    /// Optional provider name to override the configured default. When null, uses
    /// <c>AppConfig:AI:Rag:VectorStore:Provider</c>.
    /// </param>
    /// <returns>The BM25 store implementation for the specified provider.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no keyed service is registered for the provider name.
    /// </exception>
    public IBm25Store ResolveBm25Store(string? providerOverride = null)
    {
        var provider = providerOverride
            ?? _appConfig.CurrentValue.AI.Rag.VectorStore.Provider;

        var key = provider == "azure_ai_search" ? "azure_ai_search" : "faiss";
        return _serviceProvider.GetRequiredKeyedService<IBm25Store>(key);
    }
}
