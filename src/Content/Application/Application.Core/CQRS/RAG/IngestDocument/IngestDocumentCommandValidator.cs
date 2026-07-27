using Domain.Common.Config;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>
/// Validates ingestion commands before processing. When
/// <c>AppConfig:AI:Rag:ScopedCollections</c> is enabled, any caller-supplied
/// <see cref="IngestDocumentCommand.CollectionName"/> is rejected: the effective collection
/// is derived server-side from the caller's ambient tenant, and honoring a caller-chosen
/// name would let one tenant write into another tenant's collection.
/// </summary>
public sealed class IngestDocumentCommandValidator : AbstractValidator<IngestDocumentCommand>
{
	/// <summary>Initializes validation rules for ingestion commands.</summary>
	/// <param name="appConfigMonitor">
	/// Live application configuration; consulted per validation so a runtime toggle of
	/// <c>ScopedCollections</c> takes effect without a restart.
	/// </param>
	public IngestDocumentCommandValidator(IOptionsMonitor<AppConfig> appConfigMonitor)
	{
		RuleFor(x => x.DocumentUri)
			.NotNull()
			.Must(uri => uri.Scheme is "file")
			.WithMessage("DocumentUri must use file:// scheme. HTTP/HTTPS ingestion is not yet supported.");

		RuleFor(x => x.CollectionName)
			.MaximumLength(128)
			.When(x => x.CollectionName is not null);

		RuleFor(x => x.CollectionName)
			.Null()
			.When(_ => appConfigMonitor.CurrentValue.AI.Rag.ScopedCollections.Enabled)
			.WithMessage(
				"CollectionName must not be supplied when ScopedCollections is enabled: the " +
				"target collection is derived server-side from the caller's tenant.");
	}
}
