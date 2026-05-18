using Domain.Common.Config.AI.Planner;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Infrastructure.AI.Tests.Configuration;

public sealed class PlannerOptionsTests
{
    [Fact]
    public void PlannerOptions_Defaults_CorrectValues()
    {
        var options = new PlannerOptions();

        options.Enabled.Should().BeTrue();
        options.MaxConcurrentPlans.Should().Be(50);
        options.MaxParallelSteps.Should().Be(10);
        options.PlanTimeoutMinutes.Should().Be(30);
        options.MaxSubPlanDepth.Should().Be(5);
        options.AutoMigrate.Should().BeTrue();
        options.DatabasePath.Should().Be("data/planner.db");
        options.CheckpointAfterEachStep.Should().BeTrue();
    }

    [Fact]
    public void PlannerOptions_Binding_ReadsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Planner:Enabled"] = "false",
                ["AppConfig:AI:Planner:MaxConcurrentPlans"] = "100",
                ["AppConfig:AI:Planner:MaxParallelSteps"] = "20",
                ["AppConfig:AI:Planner:PlanTimeoutMinutes"] = "60",
                ["AppConfig:AI:Planner:MaxSubPlanDepth"] = "3",
                ["AppConfig:AI:Planner:AutoMigrate"] = "false",
                ["AppConfig:AI:Planner:DatabasePath"] = "custom/path.db",
                ["AppConfig:AI:Planner:CheckpointAfterEachStep"] = "false"
            })
            .Build();

        var options = config.GetSection("AppConfig:AI:Planner").Get<PlannerOptions>()!;

        options.Enabled.Should().BeFalse();
        options.MaxConcurrentPlans.Should().Be(100);
        options.MaxParallelSteps.Should().Be(20);
        options.PlanTimeoutMinutes.Should().Be(60);
        options.MaxSubPlanDepth.Should().Be(3);
        options.AutoMigrate.Should().BeFalse();
        options.DatabasePath.Should().Be("custom/path.db");
        options.CheckpointAfterEachStep.Should().BeFalse();
    }
}
