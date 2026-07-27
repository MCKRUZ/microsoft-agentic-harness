using Domain.Common.Config.AI.RAG;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates the cross-section coherence of <see cref="RagConfig"/> — specifically the
/// contract between the GraphRag feature knobs (<see cref="GraphRagConfig"/>) and the graph
/// database backend (<see cref="GraphDatabaseConfig"/>) they depend on. Rules:
/// <list type="bullet">
///   <item>When <see cref="GraphDatabaseConfig.Enabled"/> is <c>true</c>, the
///     <see cref="GraphDatabaseConfig.Provider"/> must be a backend the DI layer actually
///     registers (currently only <c>"kuzu"</c>). Before this rule, an enabled backend with an
///     unregistered provider composed cleanly and threw on the first
///     <c>IGraphDatabaseBackend</c> resolution — a first-use landmine instead of a startup
///     failure.</item>
///   <item>When <see cref="GraphRagConfig.IndexOnIngest"/> is <c>true</c>, the graph database
///     backend must be enabled: corpus-graph indexing writes through
///     <c>IGraphRagService</c>, which exists only alongside the backend. Failing at boot is
///     kinder than silently skipping the stage on every ingest.</item>
/// </list>
/// All class defaults satisfy every rule, so hosts that omit the section keep booting
/// unchanged.
/// </summary>
/// <remarks>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core assembly —
/// no manual registration required. Wired into the startup options pipeline at
/// <c>RegisterValidatedConfigSections</c> with <c>ValidateOnStart</c>.
/// </remarks>
public sealed class RagConfigValidator : AbstractValidator<RagConfig>
{
    /// <summary>
    /// Backend provider keys registered by <c>Infrastructure.AI.RAG</c>'s
    /// <c>AddRagGraphDatabase</c>. Kept case-insensitive to match keyed-DI lookup behavior
    /// being driven by operator-typed configuration.
    /// </summary>
    private static readonly HashSet<string> RegisteredBackendProviders =
        new(StringComparer.OrdinalIgnoreCase) { "kuzu" };

    /// <summary>Initializes a new instance of the <see cref="RagConfigValidator"/> class.</summary>
    public RagConfigValidator()
    {
        When(x => x.GraphDatabase.Enabled, () =>
        {
            RuleFor(x => x.GraphDatabase.Provider)
                .Must(p => p is not null && RegisteredBackendProviders.Contains(p))
                .WithMessage(x =>
                    $"GraphDatabase.Provider '{x.GraphDatabase.Provider}' has no registered " +
                    $"IGraphDatabaseBackend. Registered providers: " +
                    $"{string.Join(", ", RegisteredBackendProviders)}. Either use a registered " +
                    "provider, disable AppConfig:AI:Rag:GraphDatabase, or register a backend " +
                    "for this key in Infrastructure.AI.RAG.");
        });

        When(x => x.GraphRag.IndexOnIngest, () =>
        {
            RuleFor(x => x.GraphDatabase.Enabled)
                .Equal(true)
                .WithMessage(
                    "GraphRag.IndexOnIngest requires the graph database backend: corpus-graph " +
                    "indexing writes through IGraphRagService, which is registered only when " +
                    "AppConfig:AI:Rag:GraphDatabase:Enabled is true. Enable the backend or turn " +
                    "off AppConfig:AI:Rag:GraphRag:IndexOnIngest.");
        });
    }
}
