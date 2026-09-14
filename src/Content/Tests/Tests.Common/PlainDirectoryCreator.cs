using Application.Common.Interfaces.Common;
using Microsoft.Extensions.Logging;

namespace Tests.Common;

/// <summary>
/// Test-only <see cref="IOwnerOnlyDirectoryCreator"/> that just calls the plain BCL method. The POSIX
/// permission-hardening behavior itself belongs to <c>OwnerOnlyDirectoryHelper</c> and is exercised by
/// its own dedicated tests in <c>Infrastructure.AI.Tests</c>; every consumer of this stand-in only
/// needs the directory to actually exist afterward.
/// </summary>
/// <remarks>
/// Shared here (/code-review finding, #671/#672/#673) rather than duplicated per test assembly — the
/// same one-line fake was hand-copied into <c>Application.Common.Tests</c>,
/// <c>Application.Core.Tests</c>, and <c>Infrastructure.AI.RAG.Tests</c> before this consolidation.
/// </remarks>
public sealed class PlainDirectoryCreator : IOwnerOnlyDirectoryCreator
{
    public void Create(string directory, ILogger? logger = null) => Directory.CreateDirectory(directory);
}
