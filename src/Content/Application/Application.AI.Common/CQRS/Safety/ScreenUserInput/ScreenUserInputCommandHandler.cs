using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Safety.ScreenUserInput;

/// <summary>
/// Handles <see cref="ScreenUserInputCommand"/>. The screening is done by the pipeline behaviors in front of it, which
/// return their failure without calling it, so reaching the handler means the text was let through.
/// </summary>
public sealed class ScreenUserInputCommandHandler : IRequestHandler<ScreenUserInputCommand, Result>
{
    /// <inheritdoc />
    public Task<Result> Handle(ScreenUserInputCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
