using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// Stands in, on an erasure request, for a configured store that holds personal data but cannot erase it (#900), so the
/// receipt says so instead of the store silently vanishing from it.
/// </summary>
/// <remarks>
/// <see cref="PersonalDataErasureCoordinator"/> recognises it and records <see cref="StoreErasureStatus.NotSupported"/>
/// without calling <see cref="EraseSubjectAsync"/>. Created by <c>AddPersonalDataEraser</c> when the resolved store carries
/// <c>[PersonalDataStore]</c> but does not implement <see cref="IPersonalDataEraser"/>; a store that keeps nothing (a null
/// object, which carries no descriptor) gets no stand-in.
/// </remarks>
/// <param name="storeName">The configured store, as a reviewer would name it.</param>
/// <param name="stage">Where the store would have run; a non-erasable conversation store must still hold back the stage after it.</param>
public sealed class NonErasableStore(string storeName, ErasureStage stage) : IPersonalDataEraser
{
    /// <summary>The fixed reason shown on the receipt.</summary>
    public const string Reason = "the store configured in this host holds personal data but cannot erase one person's";

    /// <inheritdoc />
    public string ErasureStoreName { get; } = storeName;

    /// <inheritdoc />
    public ErasureStage ErasureStage { get; } = stage;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; the coordinator never calls it.</exception>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Reason);
}
