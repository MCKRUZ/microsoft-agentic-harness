namespace Application.AI.Common.Services.Runs;

/// <summary>What one <see cref="RunCancellation.CancelAsync"/> call achieved.</summary>
/// <param name="StoppedImmediately">
/// Whether the run had not started executing, or was parked, and is now cancelled. <see langword="false"/> means it was
/// executing, or had already ended: an executing workflow has been signalled and stops at its next checkpoint, and an
/// executing evaluation, which has nothing to signal, runs to its end.
/// </param>
/// <param name="WithdrawnApprovals">How many pending approvals were withdrawn along with the run.</param>
public sealed record RunCancellationOutcome(bool StoppedImmediately, int WithdrawnApprovals);
