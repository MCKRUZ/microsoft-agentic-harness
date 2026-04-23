namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR requests that participate in retrieval auditing.
/// When a request implements this interface, the <c>RetrievalAuditBehavior</c>
/// pipeline behavior records query classification, transformed query variants,
/// and retrieval timing as OpenTelemetry Activity tags.
/// </summary>
public interface IRetrievalAuditable
{
    /// <summary>
    /// Gets the raw query text to be classified and audited.
    /// </summary>
    string QueryText { get; }
}
