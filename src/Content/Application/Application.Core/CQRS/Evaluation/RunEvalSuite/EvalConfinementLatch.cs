namespace Application.Core.CQRS.Evaluation.RunEvalSuite;

/// <summary>
/// Records whether this host verified, <em>at startup</em>, that evaluation dataset reads are confined.
/// </summary>
/// <param name="StartedConfined">
/// <see langword="true"/> only when a composition root checked the configured dataset roots during
/// startup and refused to boot without them.
/// </param>
/// <remarks>
/// <para>
/// <strong>Why this is a type and not a field on the guard.</strong> The obvious implementation — latch
/// the verdict in <c>EvalDatasetPathGuard</c>'s constructor — reads as startup-bound but is not. The
/// guard is a lazily-constructed singleton, so its constructor first runs on the <em>first eval
/// dispatch</em>, which can be long after boot. Configuration is bound with <c>reloadOnChange: true</c>,
/// so a reload that emptied <c>DatasetRoots</c> in between would have the guard latch
/// "started unconfined" and take the permissive branch — the exact downgrade the latch exists to
/// prevent, with the comment above it claiming otherwise.
/// </para>
/// <para>
/// Registering the verdict as a value fixes it at composition time, which really is startup, and puts it
/// where the fail-closed check already lives: the host that refused to boot without roots is the host
/// that declares this <see langword="true"/>. Nothing else can set it, so the guard cannot be talked out
/// of confinement by a later configuration change.
/// </para>
/// <para>
/// The default registration is <see langword="false"/> — a plain constant, deliberately not a
/// configuration read. "No host claimed a startup check" is the honest default, and it costs nothing:
/// a guard with live roots configured still confines, latch or no latch. The latch only governs whether
/// <em>removing</em> the roots can loosen anything.
/// </para>
/// </remarks>
public sealed record EvalConfinementLatch(bool StartedConfined);
