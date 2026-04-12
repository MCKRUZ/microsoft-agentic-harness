using Application.AI.Common.Exceptions;
using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.MetaHarness;

/// <summary>
/// Proposes an improved harness configuration by running an orchestrated agent
/// that reads execution traces from prior candidates.
/// </summary>
public interface IHarnessProposer
{
    /// <summary>
    /// Analyzes prior execution traces and returns a proposed harness change set.
    /// </summary>
    /// <param name="context">The current optimization run context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="HarnessProposal"/> describing the proposed changes.</returns>
    /// <exception cref="HarnessProposalParsingException">
    /// Thrown when the agent's output cannot be parsed as a valid JSON proposal.
    /// </exception>
    Task<HarnessProposal> ProposeAsync(HarnessProposerContext context, CancellationToken cancellationToken);
}
