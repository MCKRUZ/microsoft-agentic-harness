using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Models;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Safety.ScreenUserInput;

/// <summary>
/// Screens caller-supplied text through the request pipeline and does nothing else.
/// </summary>
/// <remarks>
/// <para>
/// For a host whose turns do not go through the mediator. Foundry's hosted-agent runtime, for example, calls the agent
/// directly. Every other host's turn is an <see cref="IContentScreenable"/> request, so the prompt-injection scan and
/// content safety screen it on the way to the handler. Such a host sends this command first instead. The screening is
/// the same pipeline, with the same configuration, metrics and audit, not a second copy of it.
/// </para>
/// <para>
/// A success means the text may go on to the agent. A failure carries the pipeline's refusal:
/// <see cref="ResultFailureType.GovernanceBlocked"/> from the prompt-injection scan,
/// <see cref="ResultFailureType.ContentBlocked"/> from content safety. When content safety cannot screen the text and
/// <c>AppConfig:AI:ContentSafety:BlockWhenUnavailable</c> is on, sending this throws, exactly as a turn would.
/// </para>
/// </remarks>
/// <param name="Input">The caller-supplied text to screen.</param>
public sealed record ScreenUserInputCommand(string Input) : IRequest<Result>, IContentScreenable
{
    /// <inheritdoc />
    public string ContentToScreen => Input;

    /// <inheritdoc />
    public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;
}
