namespace Application.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR requests that support idempotent execution.
/// The <see cref="Application.Common.MediatRBehaviors.IdempotencyBehavior{TRequest, TResponse}"/>
/// uses the <see cref="IdempotencyKey"/> to deduplicate retried requests.
/// </summary>
/// <remarks>
/// Implement this interface on any command that must be safe to retry without side effects.
/// The caller is responsible for supplying a stable, unique key per logical operation
/// (e.g., a client-generated GUID or a deterministic hash of the request payload).
/// </remarks>
public interface IIdempotentRequest
{
    /// <summary>
    /// A unique key identifying this specific request instance.
    /// Identical keys from retried requests return the cached response
    /// instead of re-executing the handler.
    /// </summary>
    string IdempotencyKey { get; }
}
