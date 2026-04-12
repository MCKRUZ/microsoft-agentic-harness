using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown by <see cref="Interfaces.MetaHarness.IHarnessProposer"/> when the agent's output
/// cannot be parsed as a valid JSON proposal block.
/// </summary>
/// <remarks>
/// The outer optimization loop catches this exception to mark the current candidate as
/// <c>HarnessCandidateStatus.Failed</c> and continue to the next iteration rather than
/// crashing the run.
/// </remarks>
public sealed class HarnessProposalParsingException : ApplicationExceptionBase
{
    /// <summary>Gets the raw agent output that failed to parse.</summary>
    public string RawOutput { get; }

    /// <summary>
    /// Initializes a new instance with the raw output and an optional message and inner exception.
    /// </summary>
    /// <param name="rawOutput">The unparseable agent output string.</param>
    /// <param name="message">Optional override message; defaults to a summary including output length.</param>
    /// <param name="inner">Optional inner exception (e.g. <see cref="System.Text.Json.JsonException"/>).</param>
    public HarnessProposalParsingException(string rawOutput, string? message = null, Exception? inner = null)
        : base(message ?? $"Failed to parse harness proposal from agent output. Raw output length: {rawOutput.Length}", inner)
        => RawOutput = rawOutput.Length > 500 ? rawOutput[..500] + "…[truncated]" : rawOutput;
}
