using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

public sealed class ConfigRetentionPolicyProviderTests
{
    private readonly ConfigRetentionPolicyProvider _provider;

    public ConfigRetentionPolicyProviderTests()
    {
        var config = new AppConfig
        {
            AI = new()
            {
                Rag = new()
                {
                    GraphRag = new GraphRagConfig
                    {
                        RetentionPolicies = new Dictionary<string, TimeSpan>
                        {
                            ["Fact"] = TimeSpan.FromDays(365),
                            ["SkillMetric"] = TimeSpan.FromDays(180)
                        }
                    }
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);
        _provider = new ConfigRetentionPolicyProvider(monitor);
    }

    [Fact]
    public void GetPolicy_ConfiguredType_ReturnsPolicy()
    {
        var policy = _provider.GetPolicy("Fact");

        policy.EntityType.Should().Be("Fact");
        policy.RetentionPeriod.Should().Be(TimeSpan.FromDays(365));
        policy.AllowIndefinite.Should().BeFalse();
    }

    [Fact]
    public void GetPolicy_UnknownType_ReturnsIndefinite()
    {
        var policy = _provider.GetPolicy("UnknownEntity");

        policy.EntityType.Should().Be("UnknownEntity");
        policy.AllowIndefinite.Should().BeTrue();
    }

    [Fact]
    public void GetAllPolicies_ReturnsAllConfigured()
    {
        var policies = _provider.GetAllPolicies();

        policies.Should().HaveCount(2);
        policies.Select(p => p.EntityType).Should().Contain(["Fact", "SkillMetric"]);
    }
}
