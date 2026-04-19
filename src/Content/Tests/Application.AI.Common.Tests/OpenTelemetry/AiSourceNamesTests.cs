using Application.AI.Common.OpenTelemetry.Instruments;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.OpenTelemetry;

/// <summary>
/// Tests for <see cref="AiSourceNames"/> constants ensuring telemetry subscription
/// patterns are well-formed and maintain expected values for SDK integration.
/// </summary>
public class AiSourceNamesTests
{
    [Fact]
    public void MicrosoftAgentsAI_ContainsGlobPattern()
    {
        AiSourceNames.MicrosoftAgentsAI.Should().Contain("*");
        AiSourceNames.MicrosoftAgentsAI.Should().Contain("Microsoft.Agents.AI");
    }

    [Fact]
    public void MicrosoftExtensionsAI_ContainsGlobPattern()
    {
        AiSourceNames.MicrosoftExtensionsAI.Should().Contain("*");
        AiSourceNames.MicrosoftExtensionsAI.Should().Contain("Microsoft.Extensions.AI");
    }

    [Fact]
    public void SemanticKernel_ContainsGlobPattern()
    {
        AiSourceNames.SemanticKernel.Should().EndWith("*");
        AiSourceNames.SemanticKernel.Should().Contain("Microsoft.SemanticKernel");
    }

    [Fact]
    public void AgentFrameworkExact_IsExactName()
    {
        AiSourceNames.AgentFrameworkExact.Should().NotContain("*");
        AiSourceNames.AgentFrameworkExact.Should().Be("Experimental.Microsoft.Agents.AI");
    }

    [Fact]
    public void AllConstants_AreNotNullOrEmpty()
    {
        AiSourceNames.MicrosoftAgentsAI.Should().NotBeNullOrWhiteSpace();
        AiSourceNames.MicrosoftExtensionsAI.Should().NotBeNullOrWhiteSpace();
        AiSourceNames.SemanticKernel.Should().NotBeNullOrWhiteSpace();
        AiSourceNames.AgentFrameworkExact.Should().NotBeNullOrWhiteSpace();
    }
}
