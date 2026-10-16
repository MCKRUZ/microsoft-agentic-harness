using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Tests.Fakes;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// The plan executor hands the plan id to a step executor only when the executor it resolves is an
/// <see cref="IPlanAwareStepExecutor"/> (#784). The human gate needs it to bind the escalation it queues,
/// and without it the gate refuses to queue anything. A decorator registered around the keyed executor
/// that implemented only <see cref="IPlanStepExecutor"/> would silently turn every gate into a failure,
/// and no unit test of the gate or of the plan executor in isolation would notice.
/// </summary>
public sealed class PlanAwareExecutorRegistrationTests
{
    [Fact]
    public async Task HumanGateExecutor_ResolvedThroughTheCompositionRoot_IsPlanAware()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(
            new Dictionary<string, string?>
            {
                ["AppConfig:AI:AgentFramework:ClientType"] = "AzureOpenAI",
                ["AppConfig:AI:AgentFramework:DefaultDeployment"] = "gpt-4o",
            },
            overrideServices: (services, _) =>
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory()));
        using var scope = provider.CreateScope();

        var gate = scope.ServiceProvider.GetRequiredKeyedService<IPlanStepExecutor>(StepType.HumanGate);

        gate.Should().BeAssignableTo<IPlanAwareStepExecutor>(
            "the plan executor passes the plan id only to a plan-aware executor, and an unaware gate cannot bind its escalation");
    }
}
