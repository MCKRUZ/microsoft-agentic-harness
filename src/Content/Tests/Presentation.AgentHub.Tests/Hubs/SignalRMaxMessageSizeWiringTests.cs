using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Presentation.AgentHub.Tests.Hubs;

/// <summary>
/// Regression tests for #603: SignalR's own 32KB default cap on a single hub-method invocation
/// payload had no config seam, so a consumer sending a legitimately large per-call payload (e.g.
/// a full persona/system-prompt override via <c>SetConversationSettings</c>) could not raise it
/// without a code change. <see cref="DependencyInjection.AddAgentHubServices"/> now reads
/// <c>AppConfig:AgentHub:SignalRMaxReceiveMessageSizeBytes</c> and applies it only when set, so
/// SignalR's built-in default survives untouched when the consumer has not opted in.
/// </summary>
public sealed class SignalRMaxMessageSizeWiringTests
{
    [Fact]
    public void AddAgentHubServices_NoOverrideConfigured_KeepsSignalRDefault()
    {
        using var provider = BuildProvider(maxMessageSizeBytes: null);

        var hubOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        hubOptions.MaximumReceiveMessageSize.Should().Be(
            32 * 1024,
            "an unset override must not forward null to SignalR, which reads null as 'no limit'");
    }

    [Fact]
    public void AddAgentHubServices_OverrideConfigured_AppliesConfiguredMessageSize()
    {
        using var provider = BuildProvider(maxMessageSizeBytes: 131_072);

        var hubOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        hubOptions.MaximumReceiveMessageSize.Should().Be(131_072);
    }

    // #603 code-review (round 2): the validation used to run lazily inside AddSignalR's options
    // delegate, which only fires whenever something first resolves IOptions<HubOptions> — not
    // guaranteed to happen synchronously with service registration. It now runs eagerly as part
    // of AddAgentHubServices itself, so a bad value fails composition immediately.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddAgentHubServices_ZeroOrNegativeOverride_ThrowsAtCompositionTime(long invalidBytes)
    {
        var act = () => BuildProvider(maxMessageSizeBytes: invalidBytes);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SignalRMaxReceiveMessageSizeBytes*positive*");
    }

    private static ServiceProvider BuildProvider(long? maxMessageSizeBytes)
    {
        Dictionary<string, string?>? overrides = maxMessageSizeBytes is { } bytes
            ? new() { ["AppConfig:AgentHub:SignalRMaxReceiveMessageSizeBytes"] = bytes.ToString() }
            : null;

        return AgentHubTestServiceProviderFactory.Build(overrides);
    }
}
