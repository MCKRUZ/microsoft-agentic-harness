using System.Collections.Immutable;
using Domain.Common.Config.AI.Resilience;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// Config-driven registry mapping LLM provider deployment IDs to their declared
/// capabilities. Used by <see cref="ResilientChatClient"/> to compute
/// <see cref="Domain.AI.Resilience.FallbackMetadata.DisabledCapabilities"/> when falling back.
/// </summary>
public sealed class ProviderCapabilityRegistry
{
    /// <summary>Capability key for tool/function calling support.</summary>
    public const string ToolCalling = "tool_calling";

    /// <summary>Capability key for streaming response support.</summary>
    public const string Streaming = "streaming";

    /// <summary>Capability key for vision/image input support.</summary>
    public const string Vision = "vision";

    private static readonly ProviderCapabilitiesConfig FullCapabilities = new()
    {
        SupportsToolCalling = true,
        SupportsStreaming = true,
        SupportsVision = true,
        MaxTokens = int.MaxValue
    };

    private readonly IOptionsMonitor<ResilienceConfig> _configMonitor;

    /// <summary>Creates a new capability registry reading from resilience configuration.</summary>
    /// <param name="configMonitor">Options monitor for live config reload support.</param>
    public ProviderCapabilityRegistry(IOptionsMonitor<ResilienceConfig> configMonitor)
    {
        _configMonitor = configMonitor;
    }

    /// <summary>
    /// Returns the declared capabilities for the given provider deployment ID.
    /// If the provider is not in the fallback chain config, returns a
    /// "full capability" default (all features enabled) to avoid false restrictions.
    /// </summary>
    /// <param name="deploymentId">The provider deployment identifier.</param>
    /// <returns>The provider's capability configuration.</returns>
    public ProviderCapabilitiesConfig GetCapabilities(string deploymentId)
    {
        var chain = _configMonitor.CurrentValue.FallbackChain;

        var entry = chain.FirstOrDefault(p =>
            string.Equals(p.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

        return entry?.Capabilities ?? FullCapabilities;
    }

    /// <summary>
    /// Computes the set of capability names available on the primary but NOT on the active provider.
    /// Returns an empty set if the active provider is equally or more capable.
    /// </summary>
    /// <param name="primaryDeploymentId">The primary provider's deployment ID.</param>
    /// <param name="activeDeploymentId">The currently active (fallback) provider's deployment ID.</param>
    /// <returns>Well-known capability keys that are disabled on the active provider.</returns>
    public IReadOnlySet<string> DiffCapabilities(string primaryDeploymentId, string activeDeploymentId)
    {
        var primary = GetCapabilities(primaryDeploymentId);
        var active = GetCapabilities(activeDeploymentId);

        var disabled = new HashSet<string>();

        if (primary.SupportsToolCalling && !active.SupportsToolCalling)
            disabled.Add(ToolCalling);

        if (primary.SupportsStreaming && !active.SupportsStreaming)
            disabled.Add(Streaming);

        if (primary.SupportsVision && !active.SupportsVision)
            disabled.Add(Vision);

        return disabled.Count == 0 ? ImmutableHashSet<string>.Empty : disabled;
    }
}
