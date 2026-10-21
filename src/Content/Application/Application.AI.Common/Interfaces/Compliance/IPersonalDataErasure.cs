using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Erases one person's data from every store that can find it — the knowledge graph and every registered
/// <see cref="IPersonalDataEraser"/> — and returns the receipt (#900).
/// </summary>
public interface IPersonalDataErasure
{
    /// <summary>Erases everything the harness can link to <paramref name="ownerId"/>.</summary>
    /// <param name="ownerId">The user id as issued by the identity provider; never blank.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What each store removed, and whether the request reached its full scope.</returns>
    /// <exception cref="ArgumentException"><paramref name="ownerId"/> is blank: a blank subject is never a wildcard.</exception>
    Task<ErasureReceipt> EraseOwnerAsync(string ownerId, CancellationToken cancellationToken);
}
