using Domain.Common.Compliance;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// A store that can remove everything it holds for one person, as one step of a right-to-erasure request (#900).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Implemented by the store itself, and registered as this interface wherever the store is registered.</strong>
/// The erasure coordinator receives every registration as an enumerable, so a store that is not configured in a host is
/// simply not asked. A store whose <c>[PersonalDataStore]</c> descriptor says <c>ErasureSupport.PurgeByOwner</c> must
/// implement this, and one that implements it must say so; <c>EveryPersonalDataStoreIsDeclaredTests</c> enforces both
/// directions so the descriptor and the code cannot drift apart.
/// </para>
/// <para>
/// <strong>Match the owner form your writer stored</strong> (<see cref="ErasureSubject.OwnerId"/> or
/// <see cref="ErasureSubject.CanonicalOwnerId"/>). A mismatch deletes nothing and still reports success. Never treat a
/// subject as a wildcard, and report what the store actually removed, not what was asked for.
/// </para>
/// </remarks>
public interface IPersonalDataEraser
{
    /// <summary>The store's name on the receipt, as a reviewer would say it (for example <c>conversations</c>).</summary>
    string ErasureStoreName { get; }

    /// <summary>When this store is erased relative to the subject's conversations.</summary>
    ErasureStage ErasureStage { get; }

    /// <summary>Removes everything the store holds for <paramref name="subject"/>.</summary>
    /// <param name="subject">The person being erased.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many records the store actually removed.</returns>
    Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken);
}
