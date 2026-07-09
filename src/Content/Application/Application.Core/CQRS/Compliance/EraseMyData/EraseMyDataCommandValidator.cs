using Application.AI.Common.Interfaces.KnowledgeGraph;
using FluentValidation;

namespace Application.Core.CQRS.Compliance.EraseMyData;

/// <summary>
/// Validates <see cref="EraseMyDataCommand"/> at the boundary: the destructive self-scoped erasure is
/// only admissible when an authenticated user scope is present.
/// </summary>
/// <remarks>
/// The command has no fields — the subject of the erasure is the ambient
/// <see cref="IKnowledgeScope.UserId"/>. This validator injects the request-scoped
/// <see cref="IKnowledgeScope"/> and rejects the command when no user id is established, so an
/// anonymous / user-less call is turned into a validation failure before the handler runs. The handler
/// repeats the same check as defence in depth.
/// </remarks>
public sealed class EraseMyDataCommandValidator : AbstractValidator<EraseMyDataCommand>
{
    /// <summary>Initializes a new instance of the <see cref="EraseMyDataCommandValidator"/> class.</summary>
    /// <param name="scope">The request-scoped knowledge scope supplying the caller's identity.</param>
    public EraseMyDataCommandValidator(IKnowledgeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        RuleFor(command => command)
            .Must(_ => !string.IsNullOrWhiteSpace(scope.UserId))
            .WithName("Scope")
            .WithMessage("Right-to-erasure requires an authenticated user; no user scope is present.");
    }
}
