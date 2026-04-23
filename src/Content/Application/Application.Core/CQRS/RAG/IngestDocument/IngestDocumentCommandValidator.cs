using FluentValidation;

namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>Validates ingestion commands before processing.</summary>
public sealed class IngestDocumentCommandValidator : AbstractValidator<IngestDocumentCommand>
{
	public IngestDocumentCommandValidator()
	{
		RuleFor(x => x.DocumentUri)
			.NotNull()
			.Must(uri => uri.Scheme is "file")
			.WithMessage("DocumentUri must use file:// scheme. HTTP/HTTPS ingestion is not yet supported.");

		RuleFor(x => x.CollectionName)
			.MaximumLength(128)
			.When(x => x.CollectionName is not null);
	}
}
