using Xunit;

namespace Infrastructure.AI.MCP.Tests;

public class SmokeTests
{
    [Fact]
    public void ProjectLoads_Successfully()
    {
        // Validates the test project compiles and can load the Infrastructure.AI.MCP assembly
        var assembly = typeof(Infrastructure.AI.MCP.Services.McpConnectionManager).Assembly;
        Assert.NotNull(assembly);
    }

    [Fact]
    public void ModelContextProtocol_IsTheSameMajorVersionFoundryHostRuns()
    {
        // Presentation.FoundryHost is forced to ModelContextProtocol 2.x by Microsoft.Agents.AI.Foundry.Hosting.
        // The MCP client code is compiled and tested here; if this project resolved 1.x it would run against
        // a different SDK than ships in that host (a binary swap the compiler cannot see).
        Assert.Equal(2, typeof(ModelContextProtocol.Client.McpClient).Assembly.GetName().Version!.Major);
    }
}
