namespace Domain.Common.Models.Api;

/// <summary>
/// Represents the health check result for a single API endpoint,
/// including whether it is healthy and how long the check took.
/// </summary>
/// <remarks>
/// Used by <c>ApiEndpointResolverService</c> to select the healthiest
/// endpoint from a set of alternatives during service discovery.
/// </remarks>
public sealed record EndpointHealthResult
{
    /// <summary>
    /// Gets whether the endpoint responded successfully to the health check.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets the endpoint URI that was health-checked.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Gets the time taken for the health check response.
    /// </summary>
    public TimeSpan ResponseTime { get; init; }
}
