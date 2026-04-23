using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Extracts a hierarchical skeleton tree from Markdown content by parsing ATX-style
/// headings (<c># through ######</c>). Pure C# implementation with no external
/// dependencies. The resulting <see cref="SkeletonNode"/> tree enables structure-aware
/// chunking and breadcrumb generation for <see cref="DocumentChunk.SectionPath"/>.
/// </summary>
public sealed class MarkdownStructureExtractor : IStructureExtractor
{
    private static readonly Regex HeadingPattern = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <inheritdoc />
    public SkeletonNode ExtractStructure(string markdownContent)
    {
        ArgumentNullException.ThrowIfNull(markdownContent);

        var root = new SkeletonNode
        {
            Title = "Document Root",
            Level = 0,
            StartOffset = 0,
            EndOffset = markdownContent.Length,
            Parent = null
        };

        var matches = HeadingPattern.Matches(markdownContent);

        if (matches.Count == 0)
        {
            return root with { Children = [] };
        }

        var allNodes = new List<(SkeletonNode Node, int Level)>();

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var level = match.Groups[1].Value.Length;
            var title = match.Groups[2].Value.Trim();
            var startOffset = match.Index;

            var endOffset = i + 1 < matches.Count
                ? matches[i + 1].Index
                : markdownContent.Length;

            var node = new SkeletonNode
            {
                Title = title,
                Level = level,
                StartOffset = startOffset,
                EndOffset = endOffset,
                Parent = null // Set during tree assembly
            };

            allNodes.Add((node, level));
        }

        var rootChildren = BuildTree(allNodes, root);

        return root with { Children = rootChildren };
    }

    /// <summary>
    /// Recursively assembles the flat list of heading nodes into a tree by
    /// matching heading levels to parent-child relationships.
    /// </summary>
    private static IReadOnlyList<SkeletonNode> BuildTree(
        List<(SkeletonNode Node, int Level)> nodes,
        SkeletonNode parent)
    {
        if (nodes.Count == 0) return [];

        var children = new List<SkeletonNode>();
        var i = 0;

        while (i < nodes.Count)
        {
            var (currentNode, currentLevel) = nodes[i];

            // Collect all subsequent nodes that are deeper than this one — they are descendants
            var descendantNodes = new List<(SkeletonNode Node, int Level)>();
            var j = i + 1;

            while (j < nodes.Count && nodes[j].Level > currentLevel)
            {
                descendantNodes.Add(nodes[j]);
                j++;
            }

            var nodeWithParent = currentNode with { Parent = parent };
            var grandchildren = BuildTree(descendantNodes, nodeWithParent);
            var nodeWithChildren = nodeWithParent with { Children = grandchildren };

            children.Add(nodeWithChildren);
            i = j;
        }

        return children;
    }
}
