using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Extracts a hierarchical heading structure (skeleton tree) from markdown content.
/// The skeleton tree enables structure-aware chunking and breadcrumb path generation
/// (Proxy-Pointer RAG pattern), where chunks carry their position in the document
/// hierarchy for improved retrieval context.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Parse Markdown ATX headings (<c># through ######</c>) to build the tree.
///         Each heading becomes a <see cref="SkeletonNode"/> with its depth as the level.</item>
///   <item>Non-heading content between headings belongs to the nearest preceding heading node.</item>
///   <item>The root node represents the entire document (level 0) and has no heading text.</item>
///   <item>Preserve the original character offsets in each node so <see cref="IChunkingService"/>
///         can split content at structure-aware boundaries.</item>
///   <item>This operation is CPU-bound and synchronous — no I/O or LLM calls required.</item>
/// </list>
/// </remarks>
public interface IStructureExtractor
{
    /// <summary>
    /// Builds a skeleton tree from markdown content.
    /// </summary>
    /// <param name="markdownContent">The full markdown text of the document.</param>
    /// <returns>
    /// The root <see cref="SkeletonNode"/> of the document's heading hierarchy.
    /// Children are ordered by their appearance in the document.
    /// </returns>
    SkeletonNode ExtractStructure(string markdownContent);
}
