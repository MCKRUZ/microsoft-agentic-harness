using FluentValidation;

namespace Application.Core.CQRS.RAG.SearchDocuments;

/// <summary>
/// Validates <see cref="SearchDocumentsQuery"/> at the pipeline boundary.
/// </summary>
public sealed class SearchDocumentsQueryValidator : AbstractValidator<SearchDocumentsQuery>
{
    /// <summary>Initializes validation rules for search queries.</summary>
    public SearchDocumentsQueryValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty()
            .MaximumLength(4096)
            .WithMessage("Query must be between 1 and 4096 characters.");

        RuleFor(x => x.TopK)
            .InclusiveBetween(1, 100)
            .When(x => x.TopK.HasValue)
            .WithMessage("TopK must be between 1 and 100.");

        RuleFor(x => x.CollectionName)
            .MaximumLength(128)
            .When(x => x.CollectionName is not null)
            .WithMessage("CollectionName must not exceed 128 characters.");
    }
}
