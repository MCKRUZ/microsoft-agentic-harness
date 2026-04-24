namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR requests that carry a database session ID
/// for correlating observability records in PostgreSQL.
/// </summary>
public interface IHasObservabilitySession
{
    /// <summary>Gets the database session ID for observability persistence.</summary>
    Guid ObservabilitySessionId { get; }
}
