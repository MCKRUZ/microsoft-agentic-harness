using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.Common.Helpers;
using Domain.AI.Changes;
using Domain.Common.Config.AI.Governance;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.AI.SkillTraining;
using Domain.AI.Workspace;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkspaceWriteFileTool> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="WorkspaceWriteFileTool"/> class.
    /// </summary>
    /// <param name="workspace">Ambient accessor exposing the active sandbox workspace.</param>
    /// <param name="scopeFactory">Scope factory used to resolve <see cref="IMediator"/> per submission.
    /// The tool is a keyed SINGLETON, but a mediator dispatch constructs pipeline behaviors that
    /// ctor-inject the SCOPED <c>IAgentExecutionContext</c>, so the dispatch must run inside a
    /// created scope rather than against a root-bound mediator.</param>
    /// <param name="logger">
    /// Passed to <see cref="MediatorDispatchRunner"/>, which logs a scope-creation or dispatch
    /// failure before mapping it to a failed <see cref="ToolResult"/> (#428).
    /// </param>
    public WorkspaceWriteFileTool(
        IWorkspaceContextAccessor workspace,
        IServiceScopeFactory scopeFactory,
        ILogger<WorkspaceWriteFileTool> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _logger = logger;
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
    public BlastRadius RiskTier => BlastRadius.High;

    /// <inheritdoc />
    /// <remarks>
    /// Routes through change-proposal governance rather than writing directly, but its effect — file
    /// content leaving the agent's control and landing in the workspace — is exactly the sink shape
    /// this capability names. The approval gate on the proposal is a separate control, not a
    /// substitute for this tool being classified as a sink for composition purposes.
    /// </remarks>
    public ToolCompositionCapability Capabilities => ToolCompositionCapability.WritesFiles;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

    /// <inheritdoc />
    /// <remarks>
    /// Same reasoning as <see cref="Capabilities"/> immediately above, applied to the sandbox model: the
    /// approval gate on the proposal is a separate control, not a substitute for declaring what this
    /// tool's effect actually is. Unlike <c>IacGenerateTool</c> — whose own effect really is bounded to
    /// in-process templating, hence its <c>None</c> in both capability models — this tool's submitted
    /// proposal, once approved, writes the file it names; the write is deferred, not absent.
    /// </remarks>
    public ToolCapability RequiredCapabilities => ToolCapability.FileWrite;

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

        var (relativePath, pathFailure) = ResolveRelativePath(workspace, path);
        if (pathFailure is not null)
            return pathFailure;

        var command = BuildCommand(workspace, relativePath!, content, summary, parameters);

        return await MediatorDispatchRunner.RunAsync(
            _scopeFactory,
            async (mediator, ct) =>
            {
                var result = await mediator.Send(command, ct);
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
            },
            _logger,
            ToolName,
            failureContext: relativePath,
            cancellationToken);
    }

    /// <summary>
    /// Resolves+validates <paramref name="path"/> against <paramref name="workspace"/>. The proposal
    /// records the *relative* form so the applier can re-resolve against whatever working copy it
    /// operates on — the sandbox-injected absolute path is not portable across machines/replays.
    /// </summary>
    private static (string? RelativePath, ToolResult? Failure) ResolveRelativePath(
        WorkspaceContext workspace, string path)
    {
        try
        {
            var fullPath = WorkspacePathResolver.Resolve(workspace, path);
            return (WorkspacePathResolver.ToRelative(workspace, fullPath), null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, ToolResult.Fail("Access denied: path is outside the workspace."));
        }
        catch (ArgumentException)
        {
            return (null, ToolResult.Fail("Invalid path."));
        }
    }

    private static SubmitChangeProposalCommand BuildCommand(
        WorkspaceContext workspace,
        string relativePath,
        string content,
        string summary,
        IReadOnlyDictionary<string, object?> parameters)
    {
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

        return new SubmitChangeProposalCommand
        {
            Target = target,
            Diff = [edit],
            Summary = summary,
            BlastRadius = blastRadius,
            SkillKey = skillKey,
            IsStateChange = true
        };
    }

    private static BlastRadius ParseBlastRadius(IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("blast_radius", out var value) || value is not string s)
            return BlastRadius.Low;

        // Name-only. blast_radius is supplied by the model and is the input the graded-autonomy
        // policy keys its auto-approve decision on, so it is the single most consequential
        // model-authored enum in the harness. A numeric value would parse to a radius with no row
        // in the policy map, which resolves by falling through rather than by the rule the operator
        // wrote for that radius.
        return EnumNameHelper.TryParseName<BlastRadius>(s, out var parsed)
            ? parsed
            : BlastRadius.Low;
    }
}
