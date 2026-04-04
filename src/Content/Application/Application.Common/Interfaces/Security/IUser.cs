namespace Application.Common.Interfaces.Security;

/// <summary>
/// Represents the current user context for authentication and authorization.
/// In an agentic system, the "user" may be a human, an AI agent with a service
/// identity, or a system-initiated process.
/// </summary>
/// <remarks>
/// Registered as scoped in DI. Populated by authentication middleware at the
/// presentation layer. Consumed by <c>AuthorizationBehavior</c>.
/// </remarks>
public interface IUser
{
    /// <summary>Gets the user's unique identifier, or <c>null</c> if unauthenticated.</summary>
    string? Id { get; }

    /// <summary>Gets whether the user has administrator privileges.</summary>
    bool IsAdmin { get; }
}
