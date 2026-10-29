using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// The Execution API refuses to start with an Azure AI Search document index and no endpoint (#1012). Running on, its
/// erasures would clear a placeholder and read <c>Full</c> while the person's chunks stayed in the real index. The shipped
/// settings, which name no index, start on the in-process one; every other test in this project boots that way.
/// </summary>
public sealed class DocumentIndexStartupTests
{
    [Fact]
    public void AzureAISearchWithItsEndpointLost_StopsTheHost()
    {
        const string Provider = "AppConfig__AI__Rag__VectorStore__Provider";
        const string Endpoint = "AppConfig__AI__Rag__VectorStore__Endpoint";
        Environment.SetEnvironmentVariable(Provider, "azure_ai_search");
        // An empty value, as a failed Key Vault reference or an environment override leaves it.
        Environment.SetEnvironmentVariable(Endpoint, " ");
        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var act = () => factory.Server;

            act.Should().Throw<Exception>().Which.ToString().Should().Contain("AppConfig:AI:Rag:VectorStore:Endpoint");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Provider, null);
            Environment.SetEnvironmentVariable(Endpoint, null);
        }
    }
}
