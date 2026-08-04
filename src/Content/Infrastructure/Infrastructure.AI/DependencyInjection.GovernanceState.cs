using Application.AI.Common.Interfaces.Escalation;
using Domain.Common.Config;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the durable governance-state store (SQLite): the
    /// <see cref="GovernanceStateDbContext"/> factory, its schema initializer, the outcome
    /// sealer, the retention pruner, both EF-backed store implementations, and the
    /// <see cref="IEscalationStateStore"/> selection. Everything here is passive until a
    /// <c>AppConfig:AI:Governance:DurableState</c> toggle opts in — with both toggles off
    /// (the default) no database file, directory, or connection is ever created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The toggles are read from <see cref="IOptionsMonitor{TOptions}"/> at <b>first
    /// resolution</b> of the affected service, not per call. Durability is a topology
    /// property: flipping it mid-process would split state between the in-memory and durable
    /// stores, so a change requires a host restart (documented on
    /// <c>GovernanceDurableStateConfig</c>).
    /// </para>
    /// <para>
    /// The EF-backed stores demand <see cref="SchemaInitializer{TContext}"/> as a plain
    /// constructor dependency (ValidateOnBuild-visible), and are only constructed when their
    /// toggle selects them — which is what defers schema creation until a consumer opts in.
    /// The database path is normalized and containment-checked at registration; its directory
    /// is created inside the options callback, on first context materialization.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="appConfig">The composed application configuration (for the database path).</param>
    private static void RegisterGovernanceStateServices(IServiceCollection services, AppConfig appConfig)
    {
        var dbPath = GovernanceStatePaths.Resolve(appConfig.AI.Governance.DurableState.DatabasePath);
        var connectionString = $"DataSource={dbPath}";

        services.AddDbContextFactory<GovernanceStateDbContext>(options =>
        {
            // Runs on first context materialization, not at registration — hosts that never
            // enable durable governance state get zero filesystem side effects.
            GovernanceStatePaths.EnsureDirectory(dbPath);
            options.UseSqlite(connectionString);
        });

        // One registration, because there is now one type. This used to be a pair — a derived
        // GovernanceStateSchemaInitializer plus an alias mapping the base type onto it — so that a
        // future column addition reached pre-existing databases instead of silently no-opping. The
        // base initializer reconciles added columns and indexes from the model itself, so the
        // subclass is gone and the alias with it. Resolved lazily by the stores' constructors;
        // create and reconcile are both idempotent.
        services.AddSingleton<SchemaInitializer<GovernanceStateDbContext>>();

        // TimeProvider is registered by Application.Common in composed hosts; TryAdd keeps
        // Infrastructure.AI standalone-safe without overriding a host's own clock.
        services.TryAddSingleton(TimeProvider.System);

        // Seals persisted escalation outcomes AND change proposals with the harness's HMAC
        // attestation keys, each bound to its own record id. Enabling either durable-state
        // toggle therefore carries attestation key material as a prerequisite — documented on
        // GovernanceDurableStateConfig.
        services.AddSingleton<IGovernanceRecordSealer, AttestationGovernanceRecordSealer>();

        services.AddSingleton<IGovernanceStatePruner, GovernanceStatePruner>();

        // Deferred accessor for the reconciliation hosted service. Hosted services are
        // constructed when the host is built, and constructing the pruner would construct the
        // schema initializer — whose EnsureCreated would create the database file on EVERY
        // host, including every host that never enables durability. The factory defers that
        // to the first prune, which only runs once the enable check has passed.
        services.AddSingleton<Func<IGovernanceStatePruner>>(
            sp => sp.GetRequiredService<IGovernanceStatePruner>);

        services.AddSingleton<EfCoreEscalationStateStore>();
        services.AddSingleton<NullEscalationStateStore>();
        services.AddSingleton<EfCoreChangeProposalStore>();

        // Toggle read once, at first resolution (see remarks). The Null store preserves the
        // in-memory-only escalation behavior byte-for-byte when durability is off.
        services.AddSingleton<IEscalationStateStore>(sp =>
            sp.GetRequiredService<IOptionsMonitor<AppConfig>>()
                .CurrentValue.AI.Governance.DurableState.EscalationsEnabled
                ? sp.GetRequiredService<EfCoreEscalationStateStore>()
                : sp.GetRequiredService<NullEscalationStateStore>());
    }
}
