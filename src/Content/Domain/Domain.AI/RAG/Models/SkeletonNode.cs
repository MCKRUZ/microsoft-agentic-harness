namespace Domain.AI.RAG.Models;

/// <summary>
/// A node in the structural skeleton of a parsed document, representing a heading
/// or section boundary. The skeleton is built during the parsing phase and used by
/// structure-aware chunking to split at semantically meaningful boundaries.
/// Nodes form a tree via <see cref="Children"/>, enabling breadcrumb generation
/// for <see cref="DocumentChunk.SectionPath"/>.
/// </summary>
public record SkeletonNode
{
    /// <summary>
    /// The heading or section title at this node (e.g., "Risk Factors", "Market Risk").
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// The heading level (1-6) mirroring HTML/Markdown heading semantics.
    /// Level 1 is the document title, level 2 is a major section, and so on.
    /// </summary>
    public required int Level { get; init; }

    /// <summary>
    /// The character offset in the original document where this section begins.
    /// Used to map chunks back to their source positions for citation spans.
    /// </summary>
    public required int StartOffset { get; init; }

    /// <summary>
    /// The character offset in the original document where this section ends.
    /// The range [<see cref="StartOffset"/>, <see cref="EndOffset"/>) defines
    /// the content owned by this node (excluding child sections).
    /// </summary>
    public required int EndOffset { get; init; }

    /// <summary>
    /// Child nodes representing subsections within this section.
    /// Empty for leaf nodes (sections with no further heading subdivisions).
    /// </summary>
    public IReadOnlyList<SkeletonNode> Children { get; init; } = [];

    /// <summary>
    /// The parent node in the skeleton tree. Null for root-level nodes.
    /// Set during tree construction to enable upward traversal for breadcrumb generation.
    /// </summary>
    public SkeletonNode? Parent { get; init; }

    /// <summary>
    /// Walks from this node up through its ancestors to build a breadcrumb path
    /// in the format "H1 > H2 > H3". Used to populate <see cref="DocumentChunk.SectionPath"/>.
    /// </summary>
    /// <returns>
    /// A " > " delimited string of ancestor titles from root to this node,
    /// e.g., "10-K > Risk Factors > Market Risk".
    /// </returns>
    public string GetBreadcrumb()
    {
        var segments = new List<string>();
        var current = this;

        while (current is not null)
        {
            segments.Add(current.Title);
            current = current.Parent;
        }

        segments.Reverse();
        return string.Join(" > ", segments);
    }
}
