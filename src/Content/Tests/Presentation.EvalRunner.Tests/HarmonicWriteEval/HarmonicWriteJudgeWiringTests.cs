using Application.AI.Common.Evaluation.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Presentation.EvalRunner.HarmonicWriteEval;
using Xunit;

namespace Presentation.EvalRunner.Tests.HarmonicWriteEval;

/// <summary>
/// Guards the harmonic write-eval judge wiring. The abstraction-quality column silently came back
/// blank on the first paid run because the judge resolves its model from <see cref="JudgeOptions"/> —
/// a different config section than the abstractor/consolidator (which read
/// <c>AppConfig:AI:AgentFramework</c>) — and nobody populated it, so every score soft-failed.
/// </summary>
public sealed class HarmonicWriteJudgeWiringTests
{
    [Fact]
    public void AddEvaluationDependencies_Alone_LeavesJudgeDeploymentUnconfigured()
    {
        // Reproduces the root cause: the eval framework registers JudgeOptions but never sets
        // Deployment, so GetJudgeAsync throws and DefaultLlmJudge soft-fails every quality score.
        var services = new ServiceCollection();
        services.AddEvaluationDependencies();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptionsMonitor<JudgeOptions>>()
            .CurrentValue.Deployment.Should().BeEmpty(
                "AddEvaluationDependencies leaves the judge model unconfigured on its own");
    }

    [Fact]
    public void ConfigureJudgeFromAgentFramework_BindsJudgeToAgentFrameworkModel()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppConfig>().Configure(cfg =>
        {
            cfg.AI.AgentFramework.ClientType = AIAgentFrameworkClientType.OpenAI;
            cfg.AI.AgentFramework.DefaultDeployment = "anthropic/claude-sonnet-4.6";
        });
        services.AddEvaluationDependencies();

        HarmonicWriteEvalCli.ConfigureJudgeFromAgentFramework(services);

        using var provider = services.BuildServiceProvider();
        var judge = provider.GetRequiredService<IOptionsMonitor<JudgeOptions>>().CurrentValue;

        judge.Deployment.Should().Be("anthropic/claude-sonnet-4.6",
            "the judge must reuse the same model the abstractor/consolidator exercise");
        judge.ClientType.Should().Be(AIAgentFrameworkClientType.OpenAI);
    }
}
