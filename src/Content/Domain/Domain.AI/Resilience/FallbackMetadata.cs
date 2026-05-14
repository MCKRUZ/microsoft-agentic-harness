namespace Domain.AI.Resilience;

/// <summary>
/// Metadata attached to a chat response indicating which provider served it
/// and what capabilities were lost during fallback.
/// Constructed by <c>ResilientChatClient</c> after iterating the provider chain.
/// </summary>
public sealed record FallbackMetadata
{
    /// <summary>The provider name that served the response.</summary>
    public required string ActiveProvider { get; init; }

    /// <summary>True when the response came from a non-primary provider.</summary>
    public required bool IsFallback { get; init; }

    /// <summary>Ordered list of providers that failed before <see cref="ActiveProvider"/> succeeded.</summary>
    public required IReadOnlyList<string> FailedProviders { get; init; }

    /// <summary>
    /// Features unavailable on the active provider compared to the primary.
    /// Populated by <c>ProviderCapabilityRegistry</c> diffing primary vs. active provider capabilities.
    /// </summary>
    public required IReadOnlySet<string> DisabledCapabilities { get; init; }

    /// <summary>Snapshot of all providers' health at the time of the response.</summary>
    public required IReadOnlyDictionary<string, ProviderHealthState> CircuitStates { get; init; }
}
