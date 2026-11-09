using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Safety.ScreenUserInput;

/// <summary>
/// Handles <see cref="ScreenUserInputCommand"/>. Reaching the handler means the pipeline let the text through.
/// </summary>
/// <remarks>
/// The screening happens in the behaviors in front of the handler, so there is nothing left to do here. A behavior that
/// refuses the text returns its failure without calling the handler.
/// </remarks>
public sealed class ScreenUserInputCommandHandler : IRequestHandler<ScreenUserInputCommand, Result>
{
    /// <inheritdoc />
    public Task<Result> Handle(ScreenUserInputCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
