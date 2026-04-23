namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Parses raw document content into a normalized markdown representation.
/// Implementations handle format-specific conversion (PDF, DOCX, HTML, plain text
/// to Markdown) and are registered as keyed services by their supported format
/// (e.g., <c>"pdf"</c>, <c>"docx"</c>, <c>"html"</c>).
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Each parser implementation should target a single document format family.</item>
///   <item>Return clean Markdown with heading hierarchy preserved — downstream
///         <see cref="IStructureExtractor"/> relies on <c># / ## / ###</c> headings.</item>
///   <item>Resolve relative URIs (images, links) against <paramref name="documentUri"/>
///         so extracted content is self-contained.</item>
///   <item>Throw <c>NotSupportedException</c> if the URI's extension is not in
///         <see cref="SupportedExtensions"/>.</item>
/// </list>
/// </remarks>
public interface IDocumentParser
{
    /// <summary>
    /// Parses a document from the given URI into markdown text.
    /// </summary>
    /// <param name="documentUri">
    /// The URI of the document to parse. May be a local file path (<c>file://</c>),
    /// an HTTP(S) URL, or a blob storage URI depending on the implementation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document content converted to Markdown.</returns>
    Task<string> ParseAsync(Uri documentUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the file extensions this parser supports, including the leading dot
    /// (e.g., <c>".md"</c>, <c>".pdf"</c>, <c>".docx"</c>).
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }
}
