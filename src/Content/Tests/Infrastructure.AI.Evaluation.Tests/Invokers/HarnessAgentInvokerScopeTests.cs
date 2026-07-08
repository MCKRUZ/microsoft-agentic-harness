using FluentAssertions;
using Infrastructure.AI.Evaluation;
using Infrastructure.AI.Evaluation.Invokers;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Invokers;

/// <summary>
/// Regression test for the captive root-bound <see cref="IMediator"/> in
/// <see cref="HarnessAgentInvoker"/> (audit finding H2 follow-up). The invoker
/// is a SINGLETON, but a mediator dispatch constructs the pipeline behaviors —
/// six of which ctor-inject the SCOPED <c>IAgentExecutionContext</c> — so a
/// root-bound mediator throws under <c>ValidateScopes</c>. The invoker must
/// create a scope per invocation via <see cref="IServiceScopeFactory"/>.
/// The <see cref="IMediator"/> double is registered SCOPED to encode that
/// constraint without booting the full MediatR pipeline.
/// </summary>
public sealed class HarnessAgentInvokerScopeTests
{
    [Fact]
    public void HarnessAgentInvoker_ResolvesFromRoot_UnderScopeValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Mock.Of<IMediator>());

        // Production registrations under test (Infrastructure.AI.Evaluation/DependencyInjection.cs).
        services.AddEvaluationDependencies();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        var act = () => provider.GetRequiredService<HarnessAgentInvoker>();

        act.Should().NotThrow(
            "the singleton invoker must not hold a root-bound IMediator; it must dispatch inside a created scope");
    }
}
