using Application.Common.Interfaces.Security;

namespace Presentation.Common.Security;

/// <summary>
/// Represents the system/console user identity for non-HTTP hosting scenarios.
/// Used when no authentication middleware is present (console apps, worker services).
/// </summary>
/// <remarks>
/// In HTTP hosting, replace with an <c>HttpContextUser</c> implementation that reads
/// claims from <c>HttpContext.User</c> via <c>IHttpContextAccessor</c>.
/// </remarks>
public sealed class SystemUser : IUser
{
    /// <inheritdoc />
    public string? Id => "system";

    /// <inheritdoc />
    public bool IsAdmin => true;
}
