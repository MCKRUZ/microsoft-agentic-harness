using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// The Execution API refuses to start with a document index it cannot actually use (#1012). Running on, its erasures
/// would clear a placeholder, or the in-process stores, and read <c>Full</c> while the person's chunks stayed in the real
/// index; or the first erasure would fail on a store that never resolves. The shipped settings, which name no index,
/// start on the in-process one; every other test in this project boots that way.
/// </summary>
public sealed class DocumentIndexStartupTests
{
    private const string Provider = "AppConfig__AI__Rag__VectorStore__Provider";
    private const string Endpoint = "AppConfig__AI__Rag__VectorStore__Endpoint";

    [Theory]
    // Its endpoint lost: an empty value, as a failed Key Vault reference or an environment override leaves it.
    [InlineData("azure_ai_search", " ", "AppConfig:AI:Rag:VectorStore:Endpoint")]
    // An endpoint with the in-process stores, which would answer for the index and never reach it.
    [InlineData(null, "https://svc.search.windows.net", "AppConfig:AI:Rag:VectorStore:Endpoint is set")]
    // A key that resolves to no store, here with a trailing space a secret store can add.
    [InlineData("azure_ai_search ", "https://svc.search.windows.net", "IVectorStore")]
    [InlineData("FAISS", null, "IVectorStore")]
    public void ADocumentIndexTheHostCannotUse_StopsTheHost(string? provider, string? endpoint, string reported)
    {
        var priorProvider = Environment.GetEnvironmentVariable(Provider);
        var priorEndpoint = Environment.GetEnvironmentVariable(Endpoint);
        Environment.SetEnvironmentVariable(Provider, provider);
        Environment.SetEnvironmentVariable(Endpoint, endpoint);
        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var act = () => factory.Server;

            act.Should().Throw<Exception>().Which.ToString().Should().Contain(reported);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Provider, priorProvider);
            Environment.SetEnvironmentVariable(Endpoint, priorEndpoint);
        }
    }

    [Fact]
    public void AzureAISearchWithItsEndpoint_StartsTheHost()
    {
        // The control for the refusals: building the Azure stores at startup makes no call to the index, so a host
        // configured for one still starts.
        var priorProvider = Environment.GetEnvironmentVariable(Provider);
        var priorEndpoint = Environment.GetEnvironmentVariable(Endpoint);
        Environment.SetEnvironmentVariable(Provider, "azure_ai_search");
        Environment.SetEnvironmentVariable(Endpoint, "https://svc.search.windows.net");
        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var act = () => factory.Server;

            act.Should().NotThrow();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Provider, priorProvider);
            Environment.SetEnvironmentVariable(Endpoint, priorEndpoint);
        }
    }
}
