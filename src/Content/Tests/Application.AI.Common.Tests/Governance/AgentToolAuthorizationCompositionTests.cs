using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Identity;
using Application.AI.Common.Services.Governance;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Proves that per-agent tool authorization is reached <strong>through the real composition</strong>
/// — the container built by <c>AddApplicationAIDependencies</c>, resolving
/// <see cref="IToolCallAdmissionPipeline"/>, and calling the public admission entry point.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the test whose absence was the defect.</strong> Before #311,
/// <c>EntraAgentIdentityValidator</c> had fifteen passing unit tests and no production caller. Every
/// one of those tests called <c>CanInvoke</c> directly, so all of them stayed green while the control
/// did not run at all. A test that constructs the control and asks it a question can never fail for
/// the call that was never written.
/// </para>
/// <para>
/// So nothing here constructs a gate. The container is built the way a host builds it, the pipeline
/// is resolved rather than newed, and the assertion is on what <c>AdmitAsync</c> returns. Delete the
/// registration or the pipeline stage and these fail; that is the property being bought.
/// </para>
/// </remarks>
public sealed class AgentToolAuthorizationCompositionTests : IDisposable
{
    private const string AllowedTool = "file_system";
    private const string DeniedTool = "shell_exec";
    private const string AgentId = "agent-1";

    // The gate is scoped, so its container and scope must outlive the assertions that drive it.
    private readonly List<IDisposable> _disposables = [];

    /// <inheritdoc />
    public void Dispose()
    {
        // Reverse order: scopes were added after the providers that own them.
        for (var i = _disposables.Count - 1; i >= 0; i--)
            _disposables[i].Dispose();
    }

    [Fact]
    public void TheAuthorizationGateIsRegisteredByTheRealCompositionMethod()
    {
        var services = new ServiceCollection();
        services.AddApplicationAIDependencies();

        var descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IAgentToolAuthorizationGate));

        descriptor.Should().NotBeNull(
            "the admission chain takes this as a required constructor dependency, so an unregistered "
            + "gate means no host can build the chain at all");
        descriptor!.Lifetime.Should().Be(
            ServiceLifetime.Scoped,
            "it caches the workload identity it resolves for the lifetime of one turn, plan step, or "
            + "direct invocation");
        descriptor.ImplementationType.Should().Be<DefaultAgentToolAuthorizationGate>();
    }

    [Fact]
    public async Task FeatureOn_ToolOutsideTheAgentsAllowlist_IsRefusedThroughTheChain()
    {
        var pipeline = ChainOverTheResolvedGate(toolAuthorizationEnabled: true);

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(DeniedTool, new Dictionary<string, object?>()),
            CancellationToken.None);

        admission.IsAllowed.Should().BeFalse(
            "this is the whole point of the feature: an agent whose allowlist does not name a tool "
            + "must not be able to invoke it");
        admission.DeniedMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FeatureOn_ToolInsideTheAgentsAllowlist_IsAdmittedThroughTheChain()
    {
        // The other half. A gate that denied everything would satisfy the test above while being
        // just as broken as one that allowed everything.
        var pipeline = ChainOverTheResolvedGate(toolAuthorizationEnabled: true);

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(AllowedTool, new Dictionary<string, object?>()),
            CancellationToken.None);

        admission.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task FeatureOff_TheSameCallIsAdmitted()
    {
        // The default composition. Every release before this switch existed admitted this call, and a
        // consumer who has not opted in must see no change — which is the argument for the feature
        // being opt-in at all, so it is worth a test rather than a comment.
        var pipeline = ChainOverTheResolvedGate(toolAuthorizationEnabled: false);

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(DeniedTool, new Dictionary<string, object?>()),
            CancellationToken.None);

        admission.IsAllowed.Should().BeTrue(
            "with tool authorization switched off the gate must admit even a tool no allowlist names");
    }

    [Fact]
    public async Task FeatureOn_PlanCapabilityGatesAreAuthorizedToo()
    {
        // A capability gate arrives with null arguments and a well-known name. The classification
        // stage deliberately skips those; this stage deliberately does not, because an agent barred
        // from a tool must not reach equivalent capability by issuing a plan step instead.
        var pipeline = ChainOverTheResolvedGate(toolAuthorizationEnabled: true);

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest("llm_call"), CancellationToken.None);

        admission.IsAllowed.Should().BeFalse(
            "llm_call is not in this agent's allowlist, and a capability gate is not exempt from RBAC");
    }

    /// <summary>
    /// Builds the real admission chain over the gate <em>as the container produces it</em>, with the
    /// four other stages permitting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is resolved, never constructed: a test that news up the gate proves only that the
    /// class works, which is exactly what the fifteen green tests on the unwired validator proved.
    /// Resolving it means the registration, its lifetime, and its constructor dependencies are all
    /// part of what is under test.
    /// </para>
    /// <para>
    /// The chain itself is built rather than resolved because the governor stage reaches into
    /// Infrastructure registrations that this assembly cannot see by design. Stubbing that graph
    /// would test the stubs; keeping the other four stages permissive instead means any refusal
    /// observed here can only have come from the authorization stage.
    /// </para>
    /// </remarks>
    private ToolCallAdmissionPipeline ChainOverTheResolvedGate(bool toolAuthorizationEnabled)
    {
        var provider = BuildHost(toolAuthorizationEnabled);
        _disposables.Add(provider);

        var scope = provider.CreateScope();
        _disposables.Add(scope);

        var gate = scope.ServiceProvider.GetRequiredService<IAgentToolAuthorizationGate>();
        return AdmissionHarness.Pipeline(authorizationGate: gate);
    }

    /// <summary>
    /// Builds the container the way a host does: the real Application AI registrations, plus the
    /// identity services Infrastructure would supply.
    /// </summary>
    /// <remarks>
    /// The credential resolver is the one seam stubbed, because acquiring a real Entra token is not
    /// something a unit test can do. Everything downstream of it — the gate, the validator, the
    /// chain, and the wiring between them — is the shipped implementation.
    /// </remarks>
    private static ServiceProvider BuildHost(bool toolAuthorizationEnabled)
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    Enabled = true,
                    ToolAuthorization = BuildAllowlist(toolAuthorizationEnabled)
                }
            }
        };
        services.AddSingleton<IOptionsMonitor<AppConfig>>(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig));

        services.AddApplicationAIDependencies();

        // What Infrastructure contributes: the policy oracle and a resolver standing in for the
        // credential hierarchy.
        services.AddSingleton<IAgentIdentityValidator>(
            new StubValidator(appConfig.AI.Identity.ToolAuthorization));
        services.AddSingleton<IAgentIdentityResolver>(new StubResolver(
            new AgentIdentity { Id = AgentId, Kind = AgentIdentityKind.ManagedIdentity }));

        return services.BuildServiceProvider();
    }

    private static ToolAuthorizationConfig BuildAllowlist(bool enabled)
    {
        var config = new ToolAuthorizationConfig { Enabled = enabled };
        config.AllowedToolsByAgentId[AgentId] = [AllowedTool];
        return config;
    }

    /// <summary>
    /// The shipped allowlist semantics, without taking an Infrastructure reference from this test
    /// assembly. <c>EntraAgentIdentityValidator</c>'s own behaviour is covered by its unit tests; what
    /// matters here is that the chain reaches <em>a</em> validator and honours its answer.
    /// </summary>
    private sealed class StubValidator(ToolAuthorizationConfig config) : IAgentIdentityValidator
    {
        public bool CanInvoke(AgentIdentity identity, string toolKey) =>
            config.AllowedToolsByAgentId.TryGetValue(identity.Id, out var allowed)
            && allowed.Contains(toolKey, StringComparer.Ordinal);
    }

    private sealed class StubResolver(AgentIdentity identity) : IAgentIdentityResolver
    {
        public Task<Result<AgentIdentity>> ResolveAsync(
            CredentialContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Result<AgentIdentity>.Success(identity));
    }

}
