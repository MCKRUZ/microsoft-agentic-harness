using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Models;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DirectToolInvocation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="DirectToolInvoker"/> — running a host tool synchronously for an external caller.
/// </summary>
/// <remarks>
/// <para>
/// This is the harness's most exposed execution surface: it runs host-side code because a remote caller
/// asked it to. Three properties carry almost all of the safety, and each has a test here that fails if
/// the corresponding line is deleted.
/// </para>
/// <list type="number">
///   <item><description>
///   The caller's capability envelope is armed around the call. Arming it is what switches
///   <c>ToolInvocationGovernor</c>'s enforcement on, so without it the invocation runs ungoverned.
///   </description></item>
///   <item><description>
///   A caller identity is established before the governor runs. The governor resolves permission rules
///   against that subject and denies when it is absent, so an invocation that skipped it would be
///   refused — or, worse under a future change, allowed as an unattributable call.
///   </description></item>
///   <item><description>
///   Every refusal is checked against the tool's own execution counter, not just the returned status.
///   A test asserting only the status would pass against an invoker that ran the tool and then
///   reported a denial — which is the failure worth catching, because the damage is already done by
///   the time the status is chosen.
///   </description></item>
/// </list>
/// <para>
/// See the note above the refusal tests for the one property deliberately left untested, and why
/// asserting it would have been decoration.
/// </para>
/// </remarks>
public sealed class DirectToolInvokerTests
{
    private const string Caller = "caller-1";

    private readonly GovernorRecord _governor = new();
    private readonly RecordingSanitizer _sanitizer = new();
    private readonly DirectToolInvocationConfig _config = new() { Enabled = true };
    private FakeClassificationGate? _classificationGate;

    // ---- the three load-bearing properties ------------------------------------------------------

    [Fact]
    public async Task Arms_the_callers_envelope_around_the_tool_call()
    {
        // Without this the governor sees no envelope, EnforcementActive is false, and the tool runs
        // outside the grant the caller was resolved to hold — the whole authorization model, off.
        CapabilityEnvelope? observed = null;
        var tool = Tool("alpha", onExecute: () => observed = CapabilityEnvelopeAccessor.Current);
        var envelope = new CapabilityEnvelope { AllowedTools = ["alpha"] };

        await Invoke(Request("alpha", envelope: envelope), tool);

        observed.Should().BeSameAs(envelope);
    }

    [Fact]
    public async Task Establishes_a_caller_identity_before_governance_runs()
    {
        // ToolInvocationGovernor resolves permission rules against this subject, and denies outright
        // when an envelope is armed and the subject is missing. A blank identity here is not a cosmetic
        // gap — it is the shape a governance bypass takes.
        await Invoke(Request("alpha"), Tool("alpha"));

        _governor.AgentIdWhenAuthorizing.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Attributes_two_callers_to_two_different_permission_subjects()
    {
        // A shared or constant subject would pool every caller's tool use under one identity: one
        // caller's denial history would suppress another's calls, and the audit could not say who ran
        // what.
        await Invoke(Request("alpha", owner: "alice"), Tool("alpha"));
        var alice = _governor.AgentIdWhenAuthorizing;

        await Invoke(Request("alpha", owner: "bob"), Tool("alpha"));

        _governor.AgentIdWhenAuthorizing.Should().NotBe(alice);
    }

    // NOTE: there is deliberately no test asserting that the ambient accessors are clear once the
    // invocation returns. Both are AsyncLocal<T>, and a value set inside an awaited async method is
    // never visible to the awaiting caller — the ExecutionContext is restored on return. Such an
    // assertion therefore passes whether or not the invoker cleans up anything at all, which makes it
    // decoration rather than evidence. It was written, shown to be vacuous by mutation (deleting the
    // teardown left it green), and removed. The teardown stays in the invoker because it is correct
    // defensive practice on a flow that may later start detached work, and because it matches
    // ExecuteAgentTurnCommandHandler — but it is not claimed as tested here.

    // ---- refusals -------------------------------------------------------------------------------

    [Fact]
    public async Task Refuses_every_invocation_while_direct_invocation_is_disabled()
    {
        // The gate lives in the invoker, not only in the controller, so a second in-process caller
        // cannot reach the tool path around it.
        _config.Enabled = false;
        var tool = Tool("alpha");

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Disabled);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_tool_the_envelope_does_not_grant_is_reported_as_absent()
    {
        // Not "forbidden": telling a caller that a tool exists but is off-limits lets them enumerate
        // the host's inventory one name at a time.
        var outcome = await Invoke(
            Request("alpha", envelope: new CapabilityEnvelope { AllowedTools = ["beta"] }),
            Tool("alpha"));

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
    }

    [Fact]
    public async Task A_tool_that_is_not_directly_invocable_is_reported_as_absent()
    {
        // render_* / dashboard_control / delegate_task. Granted by the envelope, but not offered here —
        // and reported identically to a tool that does not exist, for the same disclosure reason.
        var tool = Tool("alpha", directlyInvocable: false);

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_granted_invocable_tool_is_not_reported_as_absent()
    {
        // The companion to the two tests above: they would both pass against an invoker that refused
        // everything. This pins that the refusals are selective.
        var outcome = await Invoke(Request("alpha"), Tool("alpha"));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task An_operation_the_tool_does_not_declare_is_rejected_before_it_runs()
    {
        var tool = Tool("alpha", operations: ["read"]);

        var outcome = await Invoke(Request("alpha", operation: "write"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task An_operation_is_matched_without_regard_to_case()
    {
        // The catalog, the envelope and the permission resolver all match case-insensitively. A
        // stricter check here would refuse invocations the rest of the harness considers well-formed.
        var outcome = await Invoke(Request("alpha", operation: "READ"), Tool("alpha", operations: ["read"]));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task A_caller_whose_identity_cannot_be_a_permission_subject_is_refused()
    {
        // The identity becomes the permission-resolution key and the audit subject. A value carrying
        // characters the resolver does not accept must be refused, not silently substituted.
        var outcome = await Invoke(Request("alpha", owner: "who am i?"), Tool("alpha"));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
    }

    [Fact]
    public async Task A_governance_denial_stops_the_tool_running()
    {
        _governor.Deny("not permitted");
        var tool = Tool("alpha");

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Denied);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task More_parameters_than_the_ceiling_allows_are_refused()
    {
        _config.MaxParameterCount = 2;
        var tool = Tool("alpha");

        var outcome = await Invoke(
            Request("alpha") with
            {
                Parameters = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2, ["c"] = 3 }
            },
            tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
        tool.Executions.Should().Be(0);
    }

    // ---- deadlines ------------------------------------------------------------------------------

    [Fact]
    public async Task A_requested_deadline_above_the_hosts_ceiling_is_refused_not_clamped()
    {
        // Clamping would be friendlier-looking and worse: the caller would experience a timeout far
        // earlier than they asked for, with nothing in the response accounting for it.
        _config.InvocationTimeout = TimeSpan.FromSeconds(5);

        var outcome = await Invoke(
            Request("alpha") with { RequestedTimeout = TimeSpan.FromMinutes(10) }, Tool("alpha"));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
    }

    [Fact]
    public async Task A_requested_deadline_within_the_ceiling_is_honoured()
    {
        _config.InvocationTimeout = TimeSpan.FromMinutes(5);

        var outcome = await Invoke(
            Request("alpha") with { RequestedTimeout = TimeSpan.FromSeconds(30) }, Tool("alpha"));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task A_tool_that_outruns_its_deadline_is_reported_as_a_timeout()
    {
        // The control that makes a synchronous surface safe to expose at all: without it a tool that
        // never returns holds a request thread and a DI scope indefinitely.
        _config.InvocationTimeout = TimeSpan.FromMilliseconds(50);
        var tool = Tool("alpha", onExecuteAsync: async ct => await Task.Delay(TimeSpan.FromSeconds(20), ct));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.TimedOut);
    }

    [Fact]
    public async Task A_slow_authorization_hits_the_deadline_too()
    {
        // The deadline has to reach the gates, not only the tool. Governance can consult a policy
        // engine, so a deadline scoped to the tool call alone leaves total request time unbounded by
        // configuration — the caller waits indefinitely on an invocation that never reached a tool.
        _config.InvocationTimeout = TimeSpan.FromMilliseconds(50);
        _governor.Delay = TimeSpan.FromSeconds(20);
        var tool = Tool("alpha");

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.TimedOut);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_slow_classification_gate_hits_the_deadline_too()
    {
        // Same reasoning, and more likely in practice: resolving an asset's sensitivity can mean a
        // network round trip or a model call.
        _config.InvocationTimeout = TimeSpan.FromMilliseconds(50);
        _classificationGate = new FakeClassificationGate(
            ClassificationVerdict.Allow(), TimeSpan.FromSeconds(20));
        var tool = Tool("alpha");

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.TimedOut);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_timeout_is_not_reported_as_a_generic_fault()
    {
        // Distinguishable on purpose: a caller can retry a timeout with a smaller request, whereas a
        // fault means the host is broken and retrying is pointless.
        _config.InvocationTimeout = TimeSpan.FromMilliseconds(50);
        var tool = Tool("alpha", onExecuteAsync: async ct => await Task.Delay(TimeSpan.FromSeconds(20), ct));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().NotBe(DirectToolInvocationStatus.Faulted);
    }

    [Fact]
    public async Task A_caller_who_disconnects_cancels_rather_than_timing_out()
    {
        // Nobody is waiting for an answer, so the invocation unwinds instead of manufacturing one.
        using var caller = new CancellationTokenSource();
        var tool = Tool("alpha", onExecuteAsync: async ct =>
        {
            await caller.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        });

        var act = async () => await Invoke(Request("alpha"), tool, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- what comes back ------------------------------------------------------------------------

    [Fact]
    public async Task Sanitizes_the_tools_output()
    {
        // Unconditional, because this crosses a trust boundary. Tool output routinely carries paths,
        // tokens and connection strings picked up from the host's own environment.
        var tool = Tool("alpha", result: ToolResult.Ok("secret=hunter2"));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Output.Should().Be(RecordingSanitizer.Scrubbed);
    }

    [Fact]
    public async Task Sanitizes_a_failing_tools_error_message()
    {
        // The likeliest place a path or a connection string surfaces is a failure message, and it
        // crosses the same boundary the output does. Scrubbing only the success half would leave the
        // more dangerous half in the clear.
        var tool = Tool("alpha", result: ToolResult.Fail("could not open C:\\keys\\prod.pem"));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Error.Should().Be(RecordingSanitizer.Scrubbed);
    }

    [Fact]
    public async Task A_tool_that_reports_failure_is_a_completed_invocation_not_a_fault()
    {
        // "The tool ran and said no" is a different event from "the host could not run it", and a
        // caller has to be able to tell them apart to know whether retrying is worth anything.
        var tool = Tool("alpha", result: ToolResult.Fail("nope"));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.ToolFailed);
    }

    [Fact]
    public async Task A_tool_that_throws_never_returns_its_exception_text()
    {
        // Exception messages carry host paths, container ids and connection strings. The caller gets a
        // stable code; the detail goes to the log.
        var tool = Tool("alpha", onExecute: () =>
            throw new InvalidOperationException("connection string Server=prod;Password=hunter2"));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Faulted);
        outcome.Error.Should().NotContain("hunter2").And.NotContain("prod");
    }

    [Fact]
    public async Task Truncates_output_beyond_the_ceiling()
    {
        _config.MaxOutputCharacters = 10;
        var tool = Tool("alpha", result: ToolResult.Ok(new string('x', 500)));
        _sanitizer.PassThrough = true;

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Output.Should().StartWith(new string('x', 10));
        outcome.Output!.Length.Should().BeLessThan(500);
    }

    [Fact]
    public async Task Says_so_when_it_truncated()
    {
        // A caller who cannot distinguish a complete result from a prefix of one will parse the prefix
        // as complete — which is worse than being refused the result altogether.
        _config.MaxOutputCharacters = 10;
        _sanitizer.PassThrough = true;
        var tool = Tool("alpha", result: ToolResult.Ok(new string('x', 500)));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.OutputTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Still_scrubs_a_secret_that_straddles_the_truncation_boundary()
    {
        // The output is cut before it is scrubbed, for cost: scrubbing 20 MB to return 256 KiB is work
        // a remote caller can demand at will. The overlap margin is what keeps that safe — a secret
        // spanning the cut must still be inside the scanned region. Get this wrong and the surface
        // emits the first half of a key.
        _config.MaxOutputCharacters = 10;
        _sanitizer.ScrubTarget = "SECRET";
        var tool = Tool("alpha", result: ToolResult.Ok(new string('x', 10) + "SECRET" + new string('y', 20)));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Output.Should().NotContain("SECRET");
    }

    [Fact]
    public async Task Reports_truncation_for_content_dropped_before_scrubbing_even_if_the_rest_fits()
    {
        // The case that only the pre-cut can produce, and the reason the flag is not simply "did the
        // final cut fire". Output far beyond the scan window is discarded before the sanitizer ever
        // sees it; if scrubbing then shrinks what remains below the ceiling, the final cut does not
        // fire — and a flag derived from that alone would tell the caller nothing was lost, when in
        // fact most of the result was. This is also the only test that reaches the pre-cut branch at
        // all: everything else is small enough to fit inside the overlap margin.
        _config.MaxOutputCharacters = 10;
        _sanitizer.ScrubTarget = new string('z', 8197);
        var tool = Tool("alpha", result: ToolResult.Ok(new string('a', 5) + new string('z', 20_000)));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Output!.Length.Should().BeLessThanOrEqualTo(10);
        outcome.OutputTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Does_not_claim_truncation_when_scrubbing_left_nothing_dropped()
    {
        // A string barely over the ceiling that the sanitizer shortened below it lost nothing. Saying
        // otherwise sends the caller hunting for content that is all there.
        _config.MaxOutputCharacters = 100;
        _sanitizer.ScrubTarget = new string('z', 60);
        var tool = Tool("alpha", result: ToolResult.Ok(new string('a', 80) + new string('z', 60)));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Output!.Length.Should().BeLessThanOrEqualTo(100);
        outcome.OutputTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Does_not_claim_truncation_for_output_that_fits()
    {
        _config.MaxOutputCharacters = 1000;
        _sanitizer.PassThrough = true;
        var tool = Tool("alpha", result: ToolResult.Ok("short"));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.OutputTruncated.Should().BeFalse();
    }

    // ---- data classification --------------------------------------------------------------------

    [Fact]
    public async Task A_classification_block_stops_the_tool_running()
    {
        // On the agent path GovernedAIFunction consults this gate, but that wrapper sits on the
        // AIFunction the model invokes and this path never builds one. Consulted explicitly here, or
        // the DLP control silently would not apply to the surface most exposed to outside callers.
        _classificationGate = new FakeClassificationGate(
            ClassificationVerdict.Block("too sensitive"));
        var tool = Tool("alpha");

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Denied);
        tool.Executions.Should().Be(0);
    }

    [Fact]
    public async Task A_classification_allow_lets_the_tool_run()
    {
        _classificationGate = new FakeClassificationGate(ClassificationVerdict.Allow());

        var outcome = await Invoke(Request("alpha"), Tool("alpha"));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task A_redact_verdict_scrubs_the_output_before_it_leaves()
    {
        _classificationGate = new FakeClassificationGate(
            ClassificationVerdict.RedactOutput());
        _sanitizer.PassThrough = true;
        var tool = Tool("alpha", result: ToolResult.Ok("classified"));

        var outcome = await Invoke(Request("alpha"), tool);

        outcome.Output.Should().Be(FakeClassificationGate.Redacted);
    }

    [Fact]
    public async Task The_classification_gate_sees_the_invocation_parameters()
    {
        // The gate resolves which asset a call touches, and the asset identity lives in the arguments,
        // not the tool name. Handed an empty map it would classify every call as touching nothing.
        _classificationGate = new FakeClassificationGate(ClassificationVerdict.Allow());

        await Invoke(
            Request("alpha") with { Parameters = new Dictionary<string, object?> { ["path"] = "/secret" } },
            Tool("alpha"));

        _classificationGate.Observed.Should().ContainKey("path");
    }

    // ---- fixture --------------------------------------------------------------------------------

    private DirectToolInvocationRequest Request(
        string toolName,
        CapabilityEnvelope? envelope = null,
        string operation = "read",
        string owner = Caller) =>
        new()
        {
            ToolName = toolName,
            Operation = operation,
            OwnerId = owner,
            Envelope = envelope ?? new CapabilityEnvelope { AllowedTools = [toolName] }
        };

    private static ExecutableTool Tool(
        string name,
        IReadOnlyList<string>? operations = null,
        ToolResult? result = null,
        Action? onExecute = null,
        Func<CancellationToken, Task>? onExecuteAsync = null,
        bool directlyInvocable = true) =>
        new(name, operations ?? ["read"], result ?? ToolResult.Ok("ok"), onExecute, onExecuteAsync, directlyInvocable);

    /// <summary>
    /// Builds the invoker over a real container, so the scope, the scoped execution context and the
    /// keyed tool resolution are the production ones rather than stand-ins.
    /// </summary>
    private async Task<DirectToolInvocationOutcome> Invoke(
        DirectToolInvocationRequest request, ExecutableTool tool, CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolInvocationGovernor>(sp =>
            new RecordingGovernor(sp.GetRequiredService<IAgentExecutionContext>(), _governor));
        services.AddKeyedSingleton<ITool>(tool.Name, tool);
        if (_classificationGate is not null)
            services.AddSingleton<IToolClassificationGate>(_classificationGate);

        var provider = services.BuildServiceProvider();

        var sut = new DirectToolInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ToolCatalog(provider, [tool.Name], NullLogger<ToolCatalog>.Instance),
            _sanitizer,
            new StaticOptionsMonitor<DirectToolInvocationConfig>(_config),
            NullLogger<DirectToolInvoker>.Instance);

        return await sut.InvokeAsync(request, cancellationToken);
    }

    /// <summary>A tool that actually runs, and remembers whether it did.</summary>
    private sealed class ExecutableTool(
        string name,
        IReadOnlyList<string> operations,
        ToolResult result,
        Action? onExecute,
        Func<CancellationToken, Task>? onExecuteAsync,
        bool directlyInvocable) : ITool
    {
        public string Name => name;
        public string Description => "executable fake";
        public IReadOnlyList<string> SupportedOperations => operations;
        public BlastRadius RiskTier => BlastRadius.Low;
        public bool IsDirectlyInvocable => directlyInvocable;

        /// <summary>How many times the tool actually ran — the evidence a refusal really refused.</summary>
        public int Executions { get; private set; }

        public async Task<ToolResult> ExecuteAsync(
            string operation,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            Executions++;
            onExecute?.Invoke();
            if (onExecuteAsync is not null)
                await onExecuteAsync(cancellationToken);

            return result;
        }
    }

    /// <summary>
    /// A governor that answers as the test dictates and records what the world looked like when it was
    /// asked. Resolved <em>from the invocation's own scope</em> and reading the same scoped
    /// <see cref="IAgentExecutionContext"/> the real governor reads, so the arming assertions observe
    /// the production mechanism rather than a stand-in the test wired up itself.
    /// </summary>
    private sealed class RecordingGovernor(IAgentExecutionContext executionContext, GovernorRecord record)
        : IToolInvocationGovernor
    {
        public async ValueTask<ToolInvocationDecision> AuthorizeAsync(
            string toolName, CancellationToken cancellationToken)
        {
            record.AgentIdWhenAuthorizing = executionContext.AgentId;

            if (record.Delay > TimeSpan.Zero)
                await Task.Delay(record.Delay, cancellationToken);

            return record.Decision;
        }

        public GovernanceTrace GetTrace() => GovernanceTrace.Empty;

        public void Reset() { }
    }

    /// <summary>
    /// Shared between the test and the scoped governor the container builds, since the test cannot
    /// hold the scoped instance itself.
    /// </summary>
    private sealed class GovernorRecord
    {
        public ToolInvocationDecision Decision { get; set; } = ToolInvocationDecision.Allow();
        public string? AgentIdWhenAuthorizing { get; set; }

        /// <summary>Makes authorization slow, so the deadline's reach over it is observable.</summary>
        public TimeSpan Delay { get; set; }

        public void Deny(string reason) => Decision = ToolInvocationDecision.Deny(reason);
    }

    /// <summary>A sanitizer that reports having scrubbed, so "was it called" is directly observable.</summary>
    private sealed class RecordingSanitizer : ICompositeResponseSanitizer
    {
        public const string Scrubbed = "[scrubbed]";

        /// <summary>When true, returns content unchanged so length-based assertions stay meaningful.</summary>
        public bool PassThrough { get; set; }

        /// <summary>
        /// When set, behaves like a real sanitizer instead of a blunt one: removes just this substring
        /// and leaves the rest. Needed to observe <em>where</em> scrubbing happened relative to the
        /// truncation cut, which a sanitizer that replaces everything cannot show.
        /// </summary>
        public string? ScrubTarget { get; set; }

        public SanitizationResult Sanitize(string content, string? toolName = null)
        {
            if (ScrubTarget is not null)
            {
                var cleaned = content.Replace(ScrubTarget, string.Empty, StringComparison.Ordinal);
                return cleaned == content
                    ? SanitizationResult.Clean(content)
                    : SanitizationResult.WithFindings(cleaned, content, []);
            }

            return PassThrough
                ? SanitizationResult.Clean(content)
                : SanitizationResult.WithFindings(Scrubbed, content, []);
        }
    }

    private sealed class FakeClassificationGate(ClassificationVerdict verdict, TimeSpan delay = default)
        : IToolClassificationGate
    {
        public const string Redacted = "[redacted]";

        public IReadOnlyDictionary<string, object?> Observed { get; private set; } =
            new Dictionary<string, object?>();

        public async ValueTask<ClassificationVerdict> EvaluateAsync(
            string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            Observed = arguments;

            // The real gate resolves an asset's sensitivity, which can mean a network or model call —
            // hence the option to be slow.
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            return verdict;
        }

        public object? RedactResult(string toolName, object? result) => Redacted;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
