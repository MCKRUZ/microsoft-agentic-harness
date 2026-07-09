using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.KnowledgeGraph.Tests.TestSupport;

/// <summary>
/// Test double for <see cref="IAmbientRequestScope"/> that exposes a fixed request service
/// provider carrying a single <see cref="IKnowledgeScope"/>. Mirrors how the production
/// singleton graph stores (e.g. <c>ComplianceAwareGraphStore</c>) resolve the caller's owner
/// per-operation from the ambient request scope rather than capturing a scoped dependency.
/// </summary>
internal sealed class StubAmbientRequestScope : IAmbientRequestScope
{
    private readonly IServiceProvider? _current;

    private StubAmbientRequestScope(IServiceProvider? current) => _current = current;

    /// <inheritdoc />
    public IServiceProvider? Current => _current;

    /// <inheritdoc />
    public IDisposable BeginScope(IServiceProvider requestServices)
        => throw new NotSupportedException("Test stub does not support establishing nested scopes.");

    /// <summary>No ambient request scope in flight (background/system context — owner stays null).</summary>
    public static StubAmbientRequestScope None() => new(current: null);

    /// <summary>An ambient scope whose caller owner (<see cref="IKnowledgeScope.UserId"/>) is <paramref name="ownerId"/>.</summary>
    public static StubAmbientRequestScope ForOwner(string? ownerId)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IKnowledgeScope>(new StubKnowledgeScope(ownerId))
            .BuildServiceProvider();
        return new StubAmbientRequestScope(provider);
    }

    private sealed class StubKnowledgeScope(string? userId) : IKnowledgeScope
    {
        public string? UserId { get; } = userId;
        public string? TenantId => null;
        public string? DatasetId => null;
        public string? DatasetName => null;
        public string? DatasetOwnerId => null;
        public string? AgentId => null;
        public string? ConversationId => null;
    }
}
