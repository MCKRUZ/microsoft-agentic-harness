using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.AI.SkillTraining;
using MediatR;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Workspace-bound write tool. Does <strong>not</strong> mutate the working
/// copy directly. Instead, packages the request as a <see cref="ChangeEdit"/>
/// and dispatches <see cref="SubmitChangeProposalCommand"/> so the harness
/// gate pipeline + approval flow govern the change.
/// </summary>
/// <remarks>
/// <para>
/// This is the load-bearing invariant of the workspace skill: an agent that
/// reaches for <c>write_file</c> cannot bypass the PR-2 governance layer. The
/// tool returns the resulting <see cref="ChangeProposal.Id"/> so the agent can
/// reference it in follow-up actions (status checks, approvals).
/// </para>
/// <para>
/// The edit is encoded as <see cref="EditOp.Replace"/> targeting the supplied
/// path: the gate pipeline + applier interpret this as "write this content to
/// this file" (the same semantics PR-2 uses for any file-shaped change). The
/// proposal's <see cref="GitRepoTarget"/> is built from the active
/// <c>WorkspaceContext</c>, including the optional head SHA so the merge
/// applier can refuse stale proposals.
/// </para>
/// </remarks>
public sealed class WorkspaceWriteFileTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "write_file";

    private static readonly IReadOnlyList<string> Operations = ["submit"];

    private readonly IWorkspaceContextAccessor _workspace;
    private readonly IMediator _mediator;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceWriteFileTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    /// <param name="mediator">MediatR dispatcher used to submit the <see cref="SubmitChangeProposalCommand"/>.</param>
    public WorkspaceWriteFileTool(IWorkspaceContextAccessor workspace, IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(mediator);
        _workspace = workspace;
        _mediator = mediator;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Proposes a file write by submitting a ChangeProposal. Does NOT mutate the working copy directly — the proposal must pass gates and be approved before applying.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "submit", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: submit.");

        var workspace = _workspace.CurrentWorkspace;
        if (workspace is null)
            return ToolResult.Fail("No workspace context is active. write_file requires the sandbox-injected workspace.");

        if (!parameters.TryGetValue("path", out var pathValue) || pathValue is not string path || string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("Required parameter 'path' is missing or empty.");

        if (!parameters.TryGetValue("content", out var contentValue) || contentValue is not string content)
            return ToolResult.Fail("Required parameter 'content' is missing.");

        if (!parameters.TryGetValue("summary", out var summaryValue) || summaryValue is not string summary || string.IsNullOrWhiteSpace(summary))
            return ToolResult.Fail("Required parameter 'summary' is missing or empty. Summaries surface in approval prompts and audit.");

        // Resolve+validate the path. The proposal records the *relative* form so the
        // applier can re-resolve against whatever working copy it operates on — the
        // sandbox-injected absolute path is not portable across machines/replays.
        string relativePath;
        try
        {
            var fullPath = WorkspacePathResolver.Resolve(workspace, path);
            relativePath = WorkspacePathResolver.ToRelative(workspace, fullPath);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail("Access denied: path is outside the workspace.");
        }
        catch (ArgumentException)
        {
            return ToolResult.Fail("Invalid path.");
        }

        var target = new GitRepoTarget(
            workspace.RepoUrl,
            workspace.Branch,
            workspace.HeadSha,
            workingPath: relativePath);

        var edit = new ChangeEdit
        {
            Op = EditOp.Replace,
            Target = relativePath,
            Content = content
        };

        var blastRadius = ParseBlastRadius(parameters);
        var skillKey = parameters.TryGetValue("skill_key", out var sk) && sk is string sks && !string.IsNullOrWhiteSpace(sks)
            ? sks
            : null;

        var command = new SubmitChangeProposalCommand
        {
            Target = target,
            Diff = [edit],
            Summary = summary,
            BlastRadius = blastRadius,
            SkillKey = skillKey,
            IsStateChange = true
        };

        var result = await _mediator.Send(command, cancellationToken);
        if (!result.IsSuccess)
        {
            var reason = result.Errors.Count > 0
                ? string.Join("; ", result.Errors)
                : "unknown error";
            return ToolResult.Fail($"ChangeProposal submission failed: {reason}");
        }

        var proposal = result.Value!;
        return ToolResult.Ok(
            $"ChangeProposal submitted: id={proposal.Id} status={proposal.Status} target={proposal.Target.DisplayName} path={relativePath}");
    }

    private static BlastRadius ParseBlastRadius(IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("blast_radius", out var value) || value is not string s)
            return BlastRadius.Low;

        return Enum.TryParse<BlastRadius>(s, ignoreCase: true, out var parsed)
            ? parsed
            : BlastRadius.Low;
    }
}
