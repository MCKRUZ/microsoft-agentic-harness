using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Telemetry;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Proves that the OpenTelemetry <c>EnableSensitiveData</c> flag AgentFactory sets on the
/// chat-client and agent instrumentation is driven by the configured content-capture policy
/// (default OFF) rather than hardcoded on. Regression guard for the finding that prompts,
/// completions, and tool arguments were exported to every trace exporter in every deployment.
/// </summary>
public sealed class AgentFactorySensitiveDataGateTests
{
    private static IContentCapturePolicy PolicyFor(ContentCaptureConfig contentCapture)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Telemetry = new TelemetryConfig { ContentCapture = contentCapture }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        return new ContentCapturePolicy(monitor, NullLogger<ContentCapturePolicy>.Instance);
    }

    [Fact]
    public void ShouldEnableSensitiveData_DefaultConfig_ReturnsFalse()
    {
        // Default ContentCaptureConfig => master flag off, every per-attribute toggle off.
        var policy = PolicyFor(new ContentCaptureConfig());

        AgentFactory.ShouldEnableSensitiveData(policy).Should().BeFalse(
            "content capture defaults to off, so sensitive prompt/completion/tool data must not be exported");
    }

    [Fact]
    public void ShouldEnableSensitiveData_NoPolicyRegistered_ReturnsFalse()
    {
        // Secure default: absent policy (content-capture pipeline not wired) must not enable capture.
        AgentFactory.ShouldEnableSensitiveData(null).Should().BeFalse();
    }

    [Fact]
    public void ShouldEnableSensitiveData_MasterEnabledButAllAttributesOff_ReturnsFalse()
    {
        var policy = PolicyFor(new ContentCaptureConfig { Enabled = true });

        AgentFactory.ShouldEnableSensitiveData(policy).Should().BeFalse(
            "flipping only the master flag with no attribute opted in captures nothing");
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void ShouldEnableSensitiveData_ConfigFlagFlippedOn_ReturnsTrue(
        bool prompt, bool output, bool toolArgs, bool toolResult)
    {
        var policy = PolicyFor(new ContentCaptureConfig
        {
            Enabled = true,
            CapturePromptContent = prompt,
            CaptureOutputContent = output,
            CaptureToolCallArguments = toolArgs,
            CaptureToolCallResult = toolResult
        });

        AgentFactory.ShouldEnableSensitiveData(policy).Should().BeTrue(
            "enabling any sensitive content-capture attribute must flip the OTel sensitive-data flag on");
    }
}
