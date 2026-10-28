using Domain.Common;
using Domain.Common.Models;

namespace Application.Core.Compliance;

/// <summary>
/// The durable, tamper-evident trail that proves each erasure, data export and document removal was handled (#969):
/// the record <see cref="AuditedErasure"/>, <see cref="AuditedExport"/> and <see cref="AuditedDocumentRemoval"/> keep
/// for as long as the organisation must be able to show it, long after the application logs are gone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it is not <c>IAuditSink</c>.</strong> <c>IAuditSink</c> is the general audit line every audited command
/// writes, and by default it is a log line that lasts only as long as the logs (30 days). Accountability for a person's
/// data-protection request (GDPR Art. 5(2)) needs a record kept far longer. A separate type makes the difference visible
/// where it is written: these three classes write here as well as to the sink, and nothing else does, so the trail never
/// collects the rest of the application's audit traffic.
/// </para>
/// <para>
/// <strong>What may be written.</strong> Only what the three classes already put in their records: who asked, the
/// pseudonymous subject reference, the receipt or request id, completeness and per-store counts. The trail is kept for
/// good and cannot be edited, so a record must never carry a person's id or any of their data. The trail itself records a
/// named operator as a keyed reference (<see cref="ErasureActor.Operator"/>), as the other audit trails record approvers.
/// </para>
/// <para>
/// <strong>Expected failures are returned.</strong> A record that cannot be written (a busy chain, a full disk, a folder
/// it may not write) comes back as a failed <see cref="Result"/>, never <see langword="null"/>. Anything unexpected, such
/// as a trail disposed while the host shuts down, may still throw, so a caller must catch as well:
/// <c>ComplianceRecords</c> does both, logs the failure and carries on, because a broken trail must never refuse a person
/// their request.
/// </para>
/// <para>
/// <strong>Replacing it.</strong> The shipped trail is also registered as an <c>IVerifiableAuditChain</c>, so scheduled
/// verification and the compliance report walk it. A replacement must implement that interface too, or provide its own
/// verification and change that registration.
/// </para>
/// </remarks>
public interface IComplianceAuditTrail
{
    /// <summary>Appends one record to the trail.</summary>
    /// <param name="entry">The record, as the caller also writes it to <c>IAuditSink</c>.</param>
    /// <param name="actor">
    /// Who asked. Decides how the executor is recorded: a named operator as a keyed reference, every other actor as its
    /// fixed name.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success once the record is written, or a failure saying why it was not.</returns>
    Task<Result> RecordAsync(AuditEntry entry, ErasureActor actor, CancellationToken cancellationToken);
}
