using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Models;
using Domain.Common;
using Domain.Common.Helpers;
using MediatR;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Screens request content against safety policies for requests implementing
/// <see cref="IContentScreenable"/>. Blocks harmful, unsafe, or policy-violating
/// content before it reaches the handler.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: 8 (after validation — content must be structurally valid before screening).
/// </para>
/// <para>
/// Only screens <see cref="ContentScreeningTarget.Input"/>. Output screening is handled
/// at the agent orchestration loop level, where the LLM response is inspected before
/// returning to the user. The pipeline behavior handles input because it has access
/// to the typed request; output screening requires inspecting the generic response.
/// </para>
/// </remarks>
public sealed class ContentSafetyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly ITextContentSafetyService _safetyService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public ContentSafetyBehavior(ITextContentSafetyService safetyService)
    {
        _safetyService = safetyService;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IContentScreenable screenable)
            return await next();

        if (screenable.ScreeningTarget is ContentScreeningTarget.Input or ContentScreeningTarget.Both)
        {
            var result = await _safetyService.ScreenAsync(screenable.ContentToScreen, cancellationToken);
            if (result.IsBlocked)
            {
                var reason = result.BlockReason ?? "Content blocked by safety policy.";
                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.ContentBlocked), reason, out var blockedResult))
                    return blockedResult;
                throw new ContentSafetyException(reason, result.Category);
            }
        }

        return await next();
    }
}
