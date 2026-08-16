using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.KnowledgeGraph;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Infrastructure.AI.Attestation;
using Infrastructure.AI.Evaluation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the SQLite-backed <see cref="PlannerDbContext"/> with a connection string
    /// derived from the configured database path. Creates the data directory if absent.
    /// </summary>
    private static void RegisterPlannerDbContext(IServiceCollection services, AppConfig appConfig)
    {
        var dbPath = appConfig.AI.Planner.DatabasePath;
        var dataDir = Path.GetDirectoryName(Path.Combine(AppContext.BaseDirectory, dbPath))!;
        Directory.CreateDirectory(dataDir);
        var connectionString = $"DataSource={Path.Combine(AppContext.BaseDirectory, dbPath)}";

        services.AddDbContextFactory<PlannerDbContext>(options => options
            .UseSqlite(connectionString)
            .AddInterceptors(new SqliteVersionInterceptor()));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PlannerDbContext>>().CreateDbContext());

        // Ensure the schema is created before the first store operation. Idempotent, and the same
        // lifecycle as every other SQLite persistence registration here. The planner used to need a
        // subclass to add its OwnerId/TenantId columns to a pre-existing PlanGraphs table; the base
        // initializer now reconciles added columns and indexes for every subsystem from the model
        // itself, so the hand-rolled copy is gone rather than kept alongside it.
        // EfCorePlanStateStore demands the initializer as a plain constructor dependency (visible to
        // ValidateOnBuild), so resolving IPlanStateStore forces schema-create exactly once — closing
        // the "no such table" hole the first SavePlanAsync would otherwise hit.
        services.AddSingleton<SchemaInitializer<PlannerDbContext>>();
    }

    /// <summary>
    /// Registers planner services: executor, validator, generator, state store, execution context,
    /// and keyed step executors for each <see cref="StepType"/>.
    /// </summary>
    private static void RegisterPlannerServices(IServiceCollection services)
    {
        // TryAddSingleton keeps Infrastructure.AI standalone-safe (the composed hosts already
        // register TimeProvider.System via Application.Common) while letting tests or hosts that
        // registered their own clock first win resolution. A plain constructor registration —
        // rather than a factory with a GetService fallback — keeps PlanExecutor's dependencies
        // visible to ValidateOnBuild.
        services.TryAddSingleton(TimeProvider.System);

        // TryAddScoped keeps Infrastructure.AI standalone-safe: EfCorePlanStateStore stamps
        // and filters plan ownership from the ambient IKnowledgeScope, which composed hosts
        // register first (KnowledgeScopeAccessor via AddKnowledgeGraphDependencies) so that
        // registration wins resolution. Hosts without the knowledge graph layer fall back to
        // the anonymous NullKnowledgeScope — plans save unstamped (global) and reads see only
        // global plans, preserving today's single-user behavior while staying ValidateOnBuild-safe.
        services.TryAddScoped<IKnowledgeScope, NullKnowledgeScope>();

        // Singleton, deliberately: IPlanExecutor is scoped, and a cancel request arrives on a
        // different scope (and thread) from the run it targets. A scoped registry would give the
        // canceller its own empty index, so it would signal nothing and report success — the exact
        // silent no-op this registry exists to prevent.
        services.TryAddSingleton<IPlanRunCancellationRegistry, PlanRunCancellationRegistry>();

        services.AddScoped<IPlanExecutor, PlanExecutor>();

        // Singleton like IBundleRunExecutor: the executor owns no per-run state itself — it creates a
        // fresh DI scope per run and arms the caller's capability envelope + governance identity
        // around IPlanExecutor inside that scope.
        services.AddSingleton<IPlanRunExecutor, PlanRunExecutor>();

        // Shared run substrate. Singletons because a run outlives the request that queued it: the
        // store and queue are the handoff between a request thread and the dispatcher, so a scoped
        // registration would give each request its own empty queue and nothing would ever run.
        services.TryAddSingleton<IRunJobStore, InMemoryRunJobStore>();
        services.TryAddSingleton<IRunDispatchQueue, InMemoryRunDispatchQueue>();

        // Singleton for the same reason: the run publishes from the dispatcher and the watcher reads
        // from a request, so a scoped broker would give each side its own instance and every stream
        // would sit silent while the run reported into nothing.
        services.TryAddSingleton<IRunProgressBroker, InMemoryRunProgressBroker>();

        // Holds what an evaluation run needs beyond the kind-agnostic record. Registered here with the
        // rest of the substrate rather than in the host that serves evaluation over HTTP, because the
        // CQRS handlers that depend on it are discovered by assembly scanning and therefore exist in
        // EVERY host — a conditional registration makes each of those hosts fail ValidateOnBuild.
        // Being registered is not being reachable: without a RunKind.Evaluation executor no eval run
        // can execute, and the endpoints refuse unless evaluation is enabled.
        services.TryAddSingleton<IEvalRunSubmissionStore, InMemoryEvalRunSubmissionStore>();

        // The dataset name catalog. In Infrastructure because it reads directories, and registered
        // here rather than in the host that serves evaluation for the same reason as the store above:
        // the CQRS handlers that depend on it are found by assembly scanning, so they exist in every
        // host and a conditional registration would fail ValidateOnBuild in all of them.
        services.TryAddSingleton<IEvalDatasetCatalog, EvalDatasetCatalog>();

        // Every holder of run-scoped state is released when a run's record is reclaimed, through this
        // one seam rather than by the sweeper naming each of them. Both go through a named adapter:
        // TryAddEnumerable distinguishes registrations by implementation type and rejects factory
        // descriptors, so a lambda would either throw here or, as a plain AddSingleton, register again
        // on every composition — and the substrate uses TryAdd throughout precisely so composing it
        // twice is harmless.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRunReclaimListener, RunProgressReclaimListener>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRunReclaimListener, EvalSubmissionReclaimListener>());

        // Keyed by RunKind, which is what makes a new kind of work a registration rather than a
        // change to the dispatcher. Scoped: the dispatcher creates a fresh scope per run and resolves
        // the executor inside it, so each run gets its own scoped dependencies.
        services.AddKeyedScoped<IRunKindExecutor, WorkflowRunKindExecutor>(RunKind.Workflow);

        // The dispatcher is passive until something enqueues; registering it unconditionally keeps a
        // host from queueing work that nothing drains.
        services.AddHostedService<RunDispatchBackgroundService>();

        // Likewise the sweeper: without it the configured retention is a claim nothing honours, and
        // every finished run — each holding the envelope it executed under — is held for the life of
        // the process.
        services.AddHostedService<RunRecordCleanupService>();

        // And likewise the resume check: a workflow that parks on a human gate is released by nothing
        // else. Without this the approval machinery still records verdicts correctly and the plan
        // executor still knows how to act on them — but nothing ever asks it to, so an approved gate
        // waits out the parked-run ceiling and fails.
        services.AddHostedService<ParkedRunResumeService>();

        services.AddScoped<IPlanValidator, PlanValidator>();
        services.AddScoped<IPlanGenerator, LlmPlanGeneratorService>();
        services.AddScoped<IPlanStateStore, EfCorePlanStateStore>();
        services.AddScoped<PlanExecutionContext>();

        services.AddKeyedScoped<IPlanStepExecutor>(StepType.LlmCall,
            (sp, _) => sp.GetRequiredService<LlmCallStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.ToolUse,
            (sp, _) => sp.GetRequiredService<ToolUseStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.HumanGate,
            (sp, _) => sp.GetRequiredService<HumanGateStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.ConditionalBranch,
            (sp, _) => sp.GetRequiredService<ConditionalBranchStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.SubPlanInvocation,
            (sp, _) => sp.GetRequiredService<SubPlanStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.Retrieval,
            (sp, _) => sp.GetRequiredService<RetrievalPlanStepExecutor>());

        services.AddScoped<LlmCallStepExecutor>();
        services.AddScoped<ToolUseStepExecutor>();
        services.AddScoped<HumanGateStepExecutor>();
        services.AddScoped<ConditionalBranchStepExecutor>();
        services.AddScoped<SubPlanStepExecutor>();
        services.AddScoped<RetrievalPlanStepExecutor>();
    }

    /// <summary>
    /// Registers sandbox execution services: process and container executors and session
    /// factories (both keyed by <see cref="SandboxIsolationLevel"/>), Docker client,
    /// attestation, and platform-specific resource limiters.
    /// </summary>
    private static void RegisterSandboxServices(IServiceCollection services)
    {
        services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Process,
            (sp, _) => sp.GetRequiredService<ProcessSandboxExecutor>());
        services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Container,
            (sp, _) => sp.GetRequiredService<DockerSandboxExecutor>());

        services.AddScoped<ProcessSandboxExecutor>();
        services.AddScoped<DockerSandboxExecutor>();

        // #371: long-lived, duplex counterpart to ISandboxExecutor — same keying scheme, same
        // shared launch mechanics (see the two LaunchPreparer types below), used where a caller
        // needs an open conversation with a sandboxed program (e.g. a bundle-owned stdio MCP
        // server) rather than a single buffered input/output exchange.
        services.AddKeyedScoped<ISandboxSessionFactory>(SandboxIsolationLevel.Process,
            (sp, _) => sp.GetRequiredService<ProcessSandboxSessionFactory>());
        services.AddKeyedScoped<ISandboxSessionFactory>(SandboxIsolationLevel.Container,
            (sp, _) => sp.GetRequiredService<DockerSandboxSessionFactory>());

        services.AddScoped<ProcessSandboxSessionFactory>();
        services.AddScoped<DockerSandboxSessionFactory>();

        // Shared launch mechanics behind both the one-shot executor and the session factory for
        // each backend, so the two can never drift from each other's security posture.
        services.AddScoped<ProcessSandboxLaunchPreparer>();
        services.AddScoped<DockerContainerLaunchPreparer>();

        // PR-3c: sandbox-side egress preflight gate, shared by every executor/session factory.
        // The executor falls back to legacy attestation when no preflight is registered — but
        // that "no preflight" case can only happen via direct construction (e.g. tests); the
        // registration below activates a real preflight unconditionally, so the sandbox cannot
        // bypass policy by default in any composed host. Scoped because ISandboxEgressPreflight
        // itself is scoped (resolves the ambient agent identity per request).
        services.AddScoped<Application.AI.Common.Interfaces.Sandbox.ISandboxEgressPreflight, Infrastructure.AI.Sandbox.SandboxEgressPreflight>();
        services.AddScoped<SandboxEgressPreflightRunner>();

        services.AddSingleton<Docker.DotNet.IDockerClient>(_ =>
            new Docker.DotNet.DockerClientConfiguration().CreateClient());

        services.AddScoped<IAttestationService, HmacAttestationService>();

        // Options-pipeline validation for the attestation key material. Deliberately NOT
        // ValidateOnStart(): AttestationKeyOptions is unbound by default (keys come from
        // User Secrets / Key Vault, never appsettings), so eager validation would fail every
        // host that doesn't use sandbox attestation. Instead the validator fires when the
        // options are first materialized (and on every reload), turning bad key material
        // into an OptionsValidationException at the point of use.
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<AttestationKeyOptions>,
            AttestationKeyOptionsValidator>();

        if (OperatingSystem.IsWindows())
            services.AddSingleton<IProcessResourceLimiter, WindowsProcessResourceLimiter>();
        else
            services.AddSingleton<IProcessResourceLimiter, NoOpProcessResourceLimiter>();
    }
}
