using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Hosting;

/// <summary>
/// Serialises full-host tests that pass configuration to the MCP server through
/// <em>process-wide</em> environment variables.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.Main</c> reads <c>AppConfig</c> eagerly from <c>builder.Configuration</c>, before
/// <c>WebApplicationFactory</c>'s own configuration overrides apply, so a host-booting test can only
/// reach it through environment variables. Those are per-process, not per-test, and xUnit runs test
/// classes in parallel. Two classes booting hosts at the same moment would read each other's
/// overrides — one class's <c>AllowAnonymous=true</c> combined with another's <c>Type=ApiKey</c> makes
/// the host refuse to start as "contradictory", an intermittent failure that reads as an auth defect.
/// </para>
/// <para>
/// A <c>DisableParallelization</c> collection runs after every parallel collection has finished, so a
/// class in it can never boot a host alongside another class. Same pattern as
/// <c>Infrastructure.AI.Tests.ProcessEnvironmentCollection</c> (issue #269).
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    /// <summary>The collection name. Apply with <c>[Collection(ProcessEnvironmentCollection.Name)]</c>.</summary>
    public const string Name = "McpServerProcessEnvironment";
}
