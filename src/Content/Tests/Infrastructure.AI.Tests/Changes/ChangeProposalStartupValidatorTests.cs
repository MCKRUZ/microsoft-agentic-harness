using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Boot-time validator tests for <see cref="ChangeProposalStartupValidator"/>.
/// Covers PR-2 follow-up items (1) in-memory store guard and
/// (7) empty-approvers guard.
/// </summary>
public sealed class ChangeProposalStartupValidatorTests
{
    private sealed class StubEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubDurableStore : IChangeProposalStore
    {
        public Task<ChangeProposal?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult<ChangeProposal?>(null);
        public Task SaveAsync(ChangeProposal proposal, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<ChangeProposal>> ListAsync(ChangeProposalQuery q, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangeProposal>>(Array.Empty<ChangeProposal>());
    }

    private sealed class StubCustomRouter : IChangeApprovalRouter
    {
        public Task RouteAsync(ChangeProposal proposal, GateContext context, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubEscalationService : IEscalationService
    {
        public Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct) =>
            Task.FromResult(Guid.Empty);
        public Task<EscalationOutcome?> SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct) =>
            Task.FromResult<EscalationOutcome?>(null);
        public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct) =>
            Task.FromResult<EscalationRequest?>(null);
        public Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EscalationRequest>>([]);
        public Task<EscalationOutcome> CancelEscalationAsync(Guid escalationId, string reason, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private static AppConfig MakeConfig(bool enabled, bool allowInMemory = false, params string[] approvers)
    {
        var cfg = new AppConfig();
        cfg.AI.Changes.Enabled = enabled;
        cfg.AI.Changes.AllowInMemoryStoreOutsideDevelopment = allowInMemory;
        cfg.AI.Changes.DefaultApprovers = [.. approvers];
        return cfg;
    }

    private static IOptionsMonitor<AppConfig> Monitor(AppConfig cfg)
        => new TestConfig.StaticOptionsMonitor<AppConfig>(cfg);

    private static EscalationServiceApprovalRouter NewDefaultRouter(AppConfig cfg) =>
        new(
            new StubEscalationService(),
            Monitor(cfg),
            TimeProvider.System,
            NullLogger<EscalationServiceApprovalRouter>.Instance);

    private static ChangeProposalStartupValidator NewSut(
        IChangeProposalStore store,
        IChangeApprovalRouter router,
        IHostEnvironment? env,
        AppConfig cfg)
    {
        var services = new ServiceCollection();
        if (env is not null)
        {
            services.AddSingleton(env);
        }
        return new(
            store,
            router,
            services.BuildServiceProvider(),
            Monitor(cfg),
            NullLogger<ChangeProposalStartupValidator>.Instance);
    }

    [Fact]
    public async Task StartAsync_PipelineDisabled_NoOps()
    {
        var cfg = MakeConfig(enabled: false);
        var sut = NewSut(
            new InMemoryChangeProposalStore(),
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Production" },
            cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_InMemoryStore_Development_LogsAndAllows()
    {
        var cfg = MakeConfig(enabled: true, approvers: "user-1");
        var sut = NewSut(
            new InMemoryChangeProposalStore(),
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Development" },
            cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_InMemoryStore_ProductionWithoutOptIn_Throws()
    {
        var cfg = MakeConfig(enabled: true, allowInMemory: false, approvers: "user-1");
        var sut = NewSut(
            new InMemoryChangeProposalStore(),
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Production" },
            cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("InMemoryChangeProposalStore");
        ex.Which.Message.Should().Contain("AllowInMemoryStoreOutsideDevelopment");
    }

    [Fact]
    public async Task StartAsync_InMemoryStore_ProductionWithExplicitOptIn_Allows()
    {
        var cfg = MakeConfig(enabled: true, allowInMemory: true, approvers: "user-1");
        var sut = NewSut(
            new InMemoryChangeProposalStore(),
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Production" },
            cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_DurableStore_Production_Allows()
    {
        // Consumer wired a durable store — the in-memory guard doesn't apply.
        var cfg = MakeConfig(enabled: true, approvers: "user-1");
        var sut = NewSut(
            new StubDurableStore(),
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Production" },
            cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_DefaultRouterWithEmptyApprovers_Throws()
    {
        var cfg = MakeConfig(enabled: true); // no approvers
        var sut = NewSut(
            new StubDurableStore(), // durable store so we isolate to the approver check
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Development" },
            cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("DefaultApprovers");
        ex.Which.Message.Should().Contain("EscalationServiceApprovalRouter");
    }

    [Fact]
    public async Task StartAsync_CustomRouterWithEmptyApprovers_Allows()
    {
        // A custom router supplies its own approver list; the default-router
        // guard shouldn't fire.
        var cfg = MakeConfig(enabled: true); // no approvers
        var sut = NewSut(
            new StubDurableStore(),
            new StubCustomRouter(),
            new StubEnv { EnvironmentName = "Development" },
            cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_NoHostEnvironmentRegistered_TreatedAsUnknown_AppliesProductionRule()
    {
        // DI test rigs that enumerate hosted services without registering
        // IHostEnvironment must not crash at construction. At StartAsync,
        // missing env is treated as non-Development and the in-memory store
        // guard fires loud.
        var cfg = MakeConfig(enabled: true, allowInMemory: false, "user-1");
        var sut = NewSut(
            new InMemoryChangeProposalStore(),
            NewDefaultRouter(cfg),
            env: null,
            cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("'Unknown'");
        ex.Which.Message.Should().Contain("InMemoryChangeProposalStore");
    }

    [Fact]
    public async Task StartAsync_DefaultRouterWithApprovers_Allows()
    {
        var cfg = MakeConfig(enabled: true, allowInMemory: false, "user-1", "user-2");
        var sut = NewSut(
            new StubDurableStore(),
            NewDefaultRouter(cfg),
            new StubEnv { EnvironmentName = "Development" },
            cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }
}
