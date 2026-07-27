using Domain.Common.Config;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.RAG.SearchDocuments;

/// <summary>
/// Validates <see cref="SearchDocumentsQuery"/> at the pipeline boundary. When
/// <c>AppConfig:AI:Rag:ScopedCollections</c> is enabled, any caller-supplied
/// <see cref="SearchDocumentsQuery.CollectionName"/> is rejected: the collection searched
/// is derived server-side from the caller's ambient tenant, and honoring a caller-chosen
/// name would be a cross-tenant read primitive.
/// </summary>
public sealed class SearchDocumentsQueryValidator : AbstractValidator<SearchDocumentsQuery>
{
    /// <summary>Initializes validation rules for search queries.</summary>
    /// <param name="appConfigMonitor">
    /// Live application configuration; consulted per validation so a runtime toggle of
    /// <c>ScopedCollections</c> takes effect without a restart.
    /// </param>
    public SearchDocumentsQueryValidator(IOptionsMonitor<AppConfig> appConfigMonitor)
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

        RuleFor(x => x.CollectionName)
            .Null()
            .When(_ => appConfigMonitor.CurrentValue.AI.Rag.ScopedCollections.Enabled)
            .WithMessage(
                "CollectionName must not be supplied when ScopedCollections is enabled: the " +
                "collection searched is derived server-side from the caller's tenant.");
    }
}
