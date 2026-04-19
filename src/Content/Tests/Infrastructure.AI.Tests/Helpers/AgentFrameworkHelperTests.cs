using FluentAssertions;
using Infrastructure.AI.Helpers;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Tests for <see cref="AgentFrameworkHelper"/> covering Azure OpenAI and
/// OpenAI client option configuration.
/// </summary>
public sealed class AgentFrameworkHelperTests
{
    [Fact]
    public void GetAzureOpenAIClientOptions_DefaultTimeout_Is300Seconds()
    {
        var options = AgentFrameworkHelper.GetAzureOpenAIClientOptions();

        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void GetAzureOpenAIClientOptions_CustomTimeout_IsApplied()
    {
        var options = AgentFrameworkHelper.GetAzureOpenAIClientOptions(60);

        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void GetAzureOpenAIClientOptions_SetsUserAgent()
    {
        var options = AgentFrameworkHelper.GetAzureOpenAIClientOptions();

        options.UserAgentApplicationId.Should().Be("AgenticHarness/1.0");
    }

    [Fact]
    public void GetOpenAIClientOptions_DefaultTimeout_Is300Seconds()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions();

        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void GetOpenAIClientOptions_CustomTimeout_IsApplied()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions(120);

        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void GetOpenAIClientOptions_SetsUserAgent()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions();

        options.UserAgentApplicationId.Should().Be("AgenticHarness/1.0");
    }
}
