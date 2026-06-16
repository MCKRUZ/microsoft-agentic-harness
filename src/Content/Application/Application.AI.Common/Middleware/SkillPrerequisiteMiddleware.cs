using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that enforces skill prerequisites by dynamically filtering
/// tools from skills whose prerequisites haven't completed yet. Also detects when a
/// skill's completion tool is invoked and marks that skill as completed.
/// </summary>
/// <remarks>
/// <para>
/// Positioned in the chat client pipeline after <see cref="ToolDiagnosticsMiddleware"/>
/// and before the distributed cache. Each call to the LLM re-evaluates prerequisite
/// state, so tools can unlock mid-turn as prerequisites complete.
/// </para>
/// <para>
/// A skill is considered complete when:
/// <list type="bullet">
///   <item>It has no <c>CompletionTool</c> declared (always complete), or</item>
///   <item>Its <c>CompletionTool</c> has been invoked (tracked via <see cref="ISkillCompletionTracker"/>)</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SkillPrerequisiteMiddleware : DelegatingChatClient
{
    private readonly ISkillCompletionTracker _tracker;
    private readonly SkillPrerequisiteMap _map;
    private readonly string _conversationId;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillPrerequisiteMiddleware"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap.</param>
    /// <param name="tracker">Tracks which skills have completed per conversation.</param>
    /// <param name="map">Prerequisite metadata for all skills in this agent context.</param>
    /// <param name="conversationId">The conversation scope for tracking completions.</param>
    /// <param name="logger">Logger for prerequisite filtering diagnostics.</param>
    public SkillPrerequisiteMiddleware(
        IChatClient innerClient,
        ISkillCompletionTracker tracker,
        SkillPrerequisiteMap map,
        string conversationId,
        ILogger<SkillPrerequisiteMiddleware> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(map);
        _tracker = tracker;
        _map = map;
        _conversationId = conversationId;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = FilterBlockedTools(options);

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        DetectCompletions(response);

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = FilterBlockedTools(options);

        var invokedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var chunk in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var name in chunk.Contents
                         .OfType<FunctionCallContent>()
                         .Select(fc => fc.Name)
                         .Where(name => !string.IsNullOrEmpty(name)))
            {
                invokedTools.Add(name);
            }

            yield return chunk;
        }

        // The streaming override must keep the same completion-detection semantics as the
        // non-streaming path; otherwise a skill's CompletionTool invoked while streaming would
        // never unlock its dependent skills for the life of the conversation.
        MarkCompletedSkills(invokedTools);
    }

    private ChatOptions? FilterBlockedTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 })
            return options;

        var blockedTools = GetBlockedToolNames();
        if (blockedTools.Count == 0)
            return options;

        var filtered = options.Tools.Where(t => !blockedTools.Contains(t.Name)).ToList();

        if (filtered.Count == options.Tools.Count)
            return options;

        _logger.LogInformation(
            "[Prerequisites] Withheld {BlockedCount} tool(s) from skills with unmet prerequisites: {BlockedTools}",
            options.Tools.Count - filtered.Count,
            string.Join(", ", blockedTools));

        var cloned = options.Clone();
        cloned.Tools = filtered;
        return cloned;
    }

    private HashSet<string> GetBlockedToolNames()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _map.Skills.Values)
        {
            if (entry.Prerequisites.Count == 0)
                continue;

            var allPrereqsMet = entry.Prerequisites.All(prereqId =>
            {
                if (_map.Skills.TryGetValue(prereqId, out var prereqEntry)
                    && prereqEntry.CompletionTool is null)
                    return true;

                return _tracker.IsCompleted(_conversationId, prereqId);
            });

            if (!allPrereqsMet)
            {
                foreach (var toolName in entry.ToolNames)
                    blocked.Add(toolName);
            }
        }

        return blocked;
    }

    private void DetectCompletions(ChatResponse response)
    {
        var toolCalls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(fc => fc.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        MarkCompletedSkills(toolCalls);
    }

    private void MarkCompletedSkills(HashSet<string> toolCalls)
    {
        if (toolCalls.Count == 0)
            return;

        foreach (var entry in _map.Skills.Values)
        {
            if (entry.CompletionTool is null)
                continue;

            if (toolCalls.Contains(entry.CompletionTool)
                && !_tracker.IsCompleted(_conversationId, entry.SkillId))
            {
                _tracker.MarkCompleted(_conversationId, entry.SkillId);

                _logger.LogInformation(
                    "[Prerequisites] Skill '{SkillId}' marked complete via tool '{CompletionTool}'",
                    entry.SkillId, entry.CompletionTool);
            }
        }
    }
}
