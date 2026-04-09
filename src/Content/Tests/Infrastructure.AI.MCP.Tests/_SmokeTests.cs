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
}
