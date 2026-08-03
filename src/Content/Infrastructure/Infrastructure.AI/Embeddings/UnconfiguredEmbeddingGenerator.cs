using Microsoft.Extensions.AI;

namespace Infrastructure.AI.Embeddings;

/// <summary>
/// Sentinel <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> registered when
/// no embedding-capable provider is configured. Satisfies the DI graph so the
/// host can boot and serve non-RAG features, but throws a clear, actionable
/// error the moment any RAG or knowledge-graph code path actually tries to
/// generate embeddings.
/// </summary>
/// <remarks>
/// This appears when the configured chat <see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType"/>
/// has no embeddings API (e.g. <c>Anthropic</c>, <c>AzureAIInference</c>,
/// <c>Echo</c>) and <c>AppConfig:AI:Embedding</c> is left unset. Configure that
/// section with a provider that exposes embeddings to enable RAG.
/// </remarks>
internal sealed class UnconfiguredEmbeddingGenerator
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly string _chatClientType;

    public UnconfiguredEmbeddingGenerator(string chatClientType)
    {
        _chatClientType = chatClientType;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw BuildException();

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose() { }

    private InvalidOperationException BuildException() => new(
        $"No embedding provider is configured. The chat client type '{_chatClientType}' " +
        "does not expose an embeddings API, and AppConfig:AI:Embedding has not been set. " +
        "Configure AppConfig:AI:Embedding with ClientType=AzureOpenAI (or OpenAI), " +
        "Endpoint, ApiKey, and Deployment to enable RAG and knowledge-graph features.");
}
