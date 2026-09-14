using Application.Common.Interfaces.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// Public DI-facing wrapper over <see cref="OwnerOnlyDirectoryHelper"/>, so a caller outside
/// <c>Infrastructure.AI</c> — which cannot see that class at all, since it is <see langword="internal"/>
/// and Clean Architecture forbids Application-layer code from referencing Infrastructure directly — gets
/// the same owner-only guarantee via <see cref="IOwnerOnlyDirectoryCreator"/> (#671, #672, #673).
/// </summary>
/// <remarks>
/// Deliberately has NO constructor dependencies — see <see cref="IOwnerOnlyDirectoryCreator"/>'s own
/// remarks on why a constructor-injected <see cref="ILogger{TCategoryName}"/> here would risk a
/// circular dependency with <c>ILoggerFactory</c> for the two logger-provider consumers (#672).
/// </remarks>
public sealed class OwnerOnlyDirectoryCreator : IOwnerOnlyDirectoryCreator
{
    /// <inheritdoc />
    public void Create(string directory, ILogger? logger = null) =>
        OwnerOnlyDirectoryHelper.Create(directory, logger);
}
