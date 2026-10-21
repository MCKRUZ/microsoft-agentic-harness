namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// An <see cref="IPersonalDataEraser"/> whose deleted bytes can outlive the delete, such as a SQLite store in
/// write-ahead-log mode, and that can clear them (#900).
/// </summary>
/// <remarks>
/// <para>
/// Scrubbing is a separate step from erasing on purpose. It runs after every stage has finished, so a scrub that cannot
/// complete (another connection mid-read) is reported as its own failed line on the receipt and never turns a committed
/// delete into a failed stage. A failed conversation stage holds later stages back, and if its rows were already gone, a
/// retry could no longer find what those stages keep through the conversation id.
/// </para>
/// <para>
/// It runs on every erasure request, whether or not this request deleted anything, so a retry clears bytes an earlier
/// request's delete left behind.
/// </para>
/// </remarks>
public interface IPersonalDataScrubber
{
    /// <summary>Clears what the store's deletes may have left in its files.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The bytes could not be cleared now; erasure should be run again.</exception>
    Task ScrubAsync(CancellationToken cancellationToken);
}
