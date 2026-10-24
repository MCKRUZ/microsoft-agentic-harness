using Domain.Common.Helpers;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// A conversation store refused to create a conversation whose id has the shape the harness keeps for ids it derives
/// from planned-task runs (<see cref="ReservedConversationIds"/>, #907).
/// </summary>
/// <remarks>
/// A distinct type so an entry point can tell this refusal, which the caller can fix by choosing another id, from any
/// other <see cref="ArgumentException"/>, which is a fault on the server's side.
/// </remarks>
public sealed class ReservedConversationIdException : ArgumentException
{
    /// <summary>The message every refusal carries.</summary>
    public const string Refusal = "A conversation id may not contain ':'; that shape is kept for ids the harness derives.";

    /// <summary>Creates the refusal.</summary>
    /// <param name="paramName">The parameter that carried the id.</param>
    public ReservedConversationIdException(string paramName)
        : base(Refusal, paramName)
    {
    }
}
