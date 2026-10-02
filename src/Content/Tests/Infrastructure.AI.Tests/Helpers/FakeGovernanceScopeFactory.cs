using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// A minimal <see cref="IServiceScopeFactory"/> for tests exercising code that opens a fresh
/// governance scope per delegated/child agent (#757) -- resolves fake
/// <see cref="IAgentExecutionContext"/> and <see cref="IToolCallAdmissionPipeline"/> instances
/// without needing a real DI container wired up.
/// </summary>
internal static class FakeGovernanceScopeFactory
{
    public static IServiceScopeFactory Create(
        out Mock<IAgentExecutionContext> context, out Mock<IToolCallAdmissionPipeline> pipeline)
    {
        context = new Mock<IAgentExecutionContext>();
        pipeline = new Mock<IToolCallAdmissionPipeline>();

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAgentExecutionContext))).Returns(context.Object);
        provider.Setup(p => p.GetService(typeof(IToolCallAdmissionPipeline))).Returns(pipeline.Object);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return factory.Object;
    }
}
