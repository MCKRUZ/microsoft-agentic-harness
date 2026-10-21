namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Turns an erased person's id into the pseudonymous reference recorded in the erasure audit trail (#900), so a record
/// can prove an erasure happened without keeping the identity it erased.
/// </summary>
public interface IErasureSubjectReference
{
    /// <summary>
    /// Returns the reference for <paramref name="subjectId"/>, or <see langword="null"/> when no reference key is
    /// configured, in which case no reference is recorded at all.
    /// </summary>
    /// <param name="subjectId">The person's id, exactly as their identity provider issues it.</param>
    /// <returns>A keyed hash of the id as lowercase hex, or <see langword="null"/>.</returns>
    string? For(string subjectId);
}
