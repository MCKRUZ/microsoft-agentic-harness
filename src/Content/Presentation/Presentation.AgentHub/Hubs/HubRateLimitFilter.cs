using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Extensions;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// SignalR hub filter that throttles the expensive, LLM-dispatching hub methods on
/// <see cref="AgentTelemetryHub"/> with a per-user token bucket.
/// </summary>
/// <remarks>
/// ASP.NET Core's <c>UseRateLimiter</c> middleware and the <c>[EnableRateLimiting]</c> attribute
/// only partition HTTP requests; SignalR hub-method invocations arrive over an already-established
/// WebSocket connection and never re-enter the HTTP middleware pipeline, so a named limiter
/// registered with <c>AddRateLimiter</c> cannot throttle them. A hub filter is the correct
/// per-invocation chokepoint — the same mechanism <see cref="KnowledgeScopeHubFilter"/> uses to
/// establish the knowledge scope.
/// <para>
/// Only the turn-dispatching methods (each of which triggers a paid LLM turn) are rate limited;
/// lifecycle and read-only methods (StartConversation, JoinConversation, etc.) pass through
/// unthrottled. Partitioning is by the caller's user id so one user cannot starve another, and a
/// rejected invocation surfaces as a <see cref="HubException"/> that the client can present to the
/// user.
/// </para>
/// </remarks>
public sealed class HubRateLimitFilter : IHubFilter, IDisposable
{
    /// <summary>Default token-bucket capacity (burst size) per user.</summary>
    public const int DefaultTokenLimit = 10;

    /// <summary>Default number of tokens replenished each <see cref="DefaultReplenishmentPeriodSeconds"/> window.</summary>
    public const int DefaultTokensPerPeriod = 10;

    /// <summary>Default replenishment period, in seconds.</summary>
    public const int DefaultReplenishmentPeriodSeconds = 60;

    private static readonly IReadOnlySet<string> RateLimitedMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AgentTelemetryHub.SendMessage),
        nameof(AgentTelemetryHub.RetryFromMessage),
        nameof(AgentTelemetryHub.EditAndResubmit),
        nameof(AgentTelemetryHub.InvokeToolViaAgent),
    };

    private readonly PartitionedRateLimiter<string> _limiter;
    private bool _disposed;

    /// <summary>
    /// Initialises the filter with a per-user token-bucket limiter using the default capacity and
    /// replenishment cadence. This is the constructor SignalR's <c>AddFilter&lt;T&gt;()</c> activates;
    /// it is marked <see cref="ActivatorUtilitiesConstructorAttribute"/> so the presence of the
    /// test-only configurable constructor does not make activation ambiguous.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public HubRateLimitFilter()
        : this(DefaultTokenLimit, DefaultTokensPerPeriod, TimeSpan.FromSeconds(DefaultReplenishmentPeriodSeconds))
    {
    }

    /// <summary>
    /// Initialises the filter with an explicit per-user token-bucket configuration. Used by tests to
    /// exercise the throttle deterministically.
    /// </summary>
    /// <param name="tokenLimit">The bucket capacity (maximum burst) per user.</param>
    /// <param name="tokensPerPeriod">The number of tokens added each replenishment period.</param>
    /// <param name="replenishmentPeriod">The replenishment cadence.</param>
    public HubRateLimitFilter(int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod)
    {
        _limiter = PartitionedRateLimiter.Create<string, string>(userId =>
            RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokenLimit,
                TokensPerPeriod = tokensPerPeriod,
                ReplenishmentPeriod = replenishmentPeriod,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!RateLimitedMethods.Contains(invocationContext.HubMethodName))
            return await next(invocationContext);

        // Partition by user id so one caller's bursts cannot exhaust another's allowance.
        // Falls back to the connection id when the principal carries no oid (e.g. dev auth bypass).
        var partitionKey = invocationContext.Context.User?.GetUserIdOrNull()
            ?? invocationContext.Context.ConnectionId;

        using var lease = await _limiter.AcquireAsync(partitionKey, permitCount: 1, invocationContext.Context.ConnectionAborted);
        if (!lease.IsAcquired)
            throw new HubException("Rate limit exceeded. Please wait before sending more messages.");

        return await next(invocationContext);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _limiter.Dispose();
    }
}
