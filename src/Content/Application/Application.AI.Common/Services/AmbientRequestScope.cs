using Application.AI.Common.Interfaces;

namespace Application.AI.Common.Services;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed implementation of <see cref="IAmbientRequestScope"/>.
/// Registered as a singleton; the ambient value flows with the async execution context, so a
/// singleton-cached agent's context providers observe the request scope established by the handler.
/// </summary>
public sealed class AmbientRequestScope : IAmbientRequestScope
{
    private static readonly AsyncLocal<IServiceProvider?> s_current = new();

    /// <inheritdoc />
    public IServiceProvider? Current => s_current.Value;

    /// <inheritdoc />
    public IDisposable BeginScope(IServiceProvider requestServices)
    {
        ArgumentNullException.ThrowIfNull(requestServices);

        return AmbientScope.Begin(s_current, requestServices);
    }
}
