using System.Diagnostics;
using Application.AI.Common.Interfaces.A2A;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Services.Agent;
using Domain.AI.A2A;
using Domain.AI.Identity;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.A2A;
using Infrastructure.AI.A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.A2A;

/// <summary>
/// Shared fixtures + helpers for the PR-7 A2A test suite. Provides span
/// capture, in-process server construction, and stub handlers / auth providers
/// without coupling tests to the full DI container.
/// </summary>
internal static class A2ATestHelpers
{
    /// <summary>Captures all activities emitted on the A2A activity source.</summary>
    public sealed class CapturedSpans : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Activities { get; } = new();

        public CapturedSpans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == A2AConventions.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = a => Activities.Add(a)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    public static AppConfig MakeConfig(A2ATransport transport = A2ATransport.InProcess) => new()
    {
        AI = new AIConfig
        {
            A2A = new A2AConfig
            {
                Enabled = true,
                Surface = new A2ASurfaceConfig { Transport = transport, MaxExtensionHeaders = 16 }
            }
        }
    };

    public static IAgentExecutionContext MakeExecutionContext(string callerAgentId)
    {
        var ctx = new AgentExecutionContext();
        ctx.SetIdentity(new AgentIdentity { Id = callerAgentId, Kind = AgentIdentityKind.Development });
        return ctx;
    }

    public static A2AEnvelope MakeEnvelope(
        string callerAgentId = "agent-a",
        string calleeAgentId = "agent-b",
        string? calleeSkill = null,
        string? correlationId = null) => new()
    {
        SchemaVersion = A2AEnvelope.CurrentSchemaVersion,
        CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
        CallerAgentId = callerAgentId,
        CallerKind = AgentIdentityKind.Development.ToString(),
        CalleeAgentId = calleeAgentId,
        CalleeSkill = calleeSkill
    };

    public static A2ARequest MakeRequest(A2AEnvelope envelope, string task = "do the thing") =>
        new() { Envelope = envelope, TaskDescription = task };

    /// <summary>
    /// Builds an in-process server backed by a single keyed handler. Returns a
    /// scope-attached <see cref="HarnessA2AServer"/> plus the underlying execution
    /// context the handler will see.
    /// </summary>
    public static (HarnessA2AServer Server, IAgentExecutionContext Context) BuildInProcessServer(
        string calleeAgentId,
        Func<A2ARequest, Task<Result<A2AResponse>>> handler,
        AppConfig? appConfig = null,
        string? calleeSkill = null)
    {
        var ctx = new AgentExecutionContext();
        var executionContext = (IAgentExecutionContext)ctx;
        var authProvider = new InProcessA2AAuthenticationProvider(executionContext);
        var spanEmitter = new A2ASpanEmitter();
        var propagator = new A2AIdentityPropagator(executionContext);
        var monitor = new TestOptionsMonitor<AppConfig>(appConfig ?? MakeConfig());

        var services = new ServiceCollection();
        var key = calleeSkill is null ? calleeAgentId : $"{calleeAgentId}:{calleeSkill}";
        services.AddKeyedSingleton<IA2ASkillHandler>(key, (_, _) => new DelegateHandler(handler));
        var provider = services.BuildServiceProvider();

        var server = new HarnessA2AServer(
            provider,
            authProvider,
            spanEmitter,
            propagator,
            monitor,
            NullLogger<HarnessA2AServer>.Instance);

        return (server, executionContext);
    }

    /// <summary>
    /// Builds an in-process client that dispatches into the supplied server.
    /// </summary>
    public static HarnessA2AClient BuildInProcessClient(
        IA2AServer server,
        IAgentExecutionContext executionContext,
        AppConfig? appConfig = null)
    {
        var authProvider = new InProcessA2AAuthenticationProvider(executionContext);
        var spanEmitter = new A2ASpanEmitter();
        var propagator = new A2AIdentityPropagator(executionContext);
        var monitor = new TestOptionsMonitor<AppConfig>(appConfig ?? MakeConfig());

        return new HarnessA2AClient(
            server,
            authProvider,
            spanEmitter,
            propagator,
            new StubHttpClientFactory(),
            monitor,
            NullLogger<HarnessA2AClient>.Instance);
    }

    private sealed class DelegateHandler : IA2ASkillHandler
    {
        private readonly Func<A2ARequest, Task<Result<A2AResponse>>> _impl;
        public DelegateHandler(Func<A2ARequest, Task<Result<A2AResponse>>> impl) => _impl = impl;
        public Task<Result<A2AResponse>> HandleAsync(A2ARequest request, CancellationToken cancellationToken)
            => _impl(request);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> for tests.</summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
