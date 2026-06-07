using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Tests.Egress.Support;

/// <summary>
/// Test double for <see cref="IAmbientRequestScope"/>. Carries a pre-built
/// <see cref="IServiceProvider"/> that holds a stub <see cref="IAgentExecutionContext"/>
/// already populated with an <see cref="AgentIdentity"/>.
/// </summary>
internal sealed class FakeAmbientRequestScope : IAmbientRequestScope
{
    public FakeAmbientRequestScope(AgentIdentity? identity)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentExecutionContext>(new FakeAgentExecutionContext(identity));
        Current = identity is null ? null : services.BuildServiceProvider();
    }

    public IServiceProvider? Current { get; }

    public IDisposable BeginScope(IServiceProvider requestServices) =>
        throw new NotSupportedException("Test fake does not support nested BeginScope.");

    private sealed class FakeAgentExecutionContext : IAgentExecutionContext
    {
        public FakeAgentExecutionContext(AgentIdentity? identity) { AgentIdentity = identity; }

        public string? AgentId => AgentIdentity?.Id;
        public string? ConversationId => null;
        public int? TurnNumber => null;
        public AgentIdentity? AgentIdentity { get; private set; }

        public void Initialize(string agentId, string conversationId, int turnNumber)
            => throw new NotSupportedException();

        public void SetIdentity(AgentIdentity identity) => AgentIdentity = identity;
    }
}
