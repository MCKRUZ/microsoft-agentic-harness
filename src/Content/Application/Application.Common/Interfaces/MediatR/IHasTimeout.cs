namespace Application.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for requests that specify a custom timeout.
/// Consumed by <c>TimeoutBehavior</c>. Requests without this interface
/// use the default timeout from <c>AppConfig.Agent.DefaultRequestTimeoutSec</c>.
/// </summary>
public interface IHasTimeout
{
    /// <summary>
    /// Gets the timeout for this request, or <c>null</c> to use the default.
    /// </summary>
    TimeSpan? Timeout { get; }
}
