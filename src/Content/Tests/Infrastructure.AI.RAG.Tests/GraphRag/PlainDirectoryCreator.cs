using Application.Common.Interfaces.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Test-only <see cref="IOwnerOnlyDirectoryCreator"/> that just calls the plain BCL method — the POSIX
/// permission-hardening behavior itself is covered by <c>OwnerOnlyDirectoryHelperTests</c> and
/// <c>DirectoryCreationGuardTests</c> in <c>Infrastructure.AI.Tests</c>; these graph-backend tests only
/// need the data directory to actually exist afterward.
/// </summary>
internal sealed class PlainDirectoryCreator : IOwnerOnlyDirectoryCreator
{
    public void Create(string directory, ILogger? logger = null) => Directory.CreateDirectory(directory);
}
