using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Bundles;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Presentation.ExecutionApi.DTOs;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Full-stack HTTP tests for the tool-catalog endpoints against the real composition, so the
/// registrations these describe are the host's actual ones rather than a fixture's.
/// </summary>
/// <remarks>
/// <para>
/// The capability envelope is stubbed rather than driven through configuration. Resolving an envelope
/// from config is already covered by the bundle tests and is not what these assert; what matters here
/// is that whatever envelope the resolver produces is the thing the catalog filters by, and that the
/// endpoint discloses nothing outside it.
/// </para>
/// </remarks>
public sealed class ToolsControllerIntegrationTests
{
    /// <summary>
    /// Matches the host's own serializer: it registers a <see cref="JsonStringEnumConverter"/>, so
    /// <c>riskTier</c> travels as a name such as <c>"Medium"</c> rather than an ordinal. Deserializing
    /// with defaults here would test a contract the host does not publish.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Boots the real host with the caller's envelope replaced by a fixed grant.
    /// </summary>
    private static WebApplicationFactory<Program> FactoryGranting(params string[] toolNames) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<ICapabilityEnvelopeResolver>(
                    new FixedEnvelopeResolver(new CapabilityEnvelope { AllowedTools = toolNames })))));

    [Fact]
    public async Task List_EnvelopeGrantingNothing_ReturnsAnEmptyCatalogNotTheInventory()
    {
        // The shipped default grants no tools. The failure this guards against is a listing that
        // ignores the envelope: it would hand every authenticated caller the host's whole tool
        // inventory, which is reconnaissance for anyone probing what this host can be made to do.
        using var factory = FactoryGranting();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/tools");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var catalog = await response.Content.ReadFromJsonAsync<ToolCatalogResponse>(WireOptions);
        catalog!.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ReturnsTheGrantedToolAndNothingElse()
    {
        using var factory = FactoryGranting("file_system");
        using var client = factory.CreateClient();

        var catalog = await client.GetFromJsonAsync<ToolCatalogResponse>("/api/tools", WireOptions);

        catalog!.Tools.Should().ContainSingle().Which.Name.Should().Be("file_system");
    }

    [Fact]
    public async Task List_GrantedEntry_CarriesTheOperationsACallerNeedsToInvokeIt()
    {
        // The catalog exists so a workflow author can write a valid ToolUse step without reading
        // the host's source. An entry with no operations would not achieve that.
        using var factory = FactoryGranting("file_system");
        using var client = factory.CreateClient();

        var catalog = await client.GetFromJsonAsync<ToolCatalogResponse>("/api/tools", WireOptions);

        var entry = catalog!.Tools.Should().ContainSingle().Subject;
        entry.Operations.Should().NotBeEmpty();
        entry.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_GrantedTool_ReturnsIt()
    {
        using var factory = FactoryGranting("file_system");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/tools/file_system");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await response.Content.ReadFromJsonAsync<ToolCatalogEntry>(WireOptions);
        entry!.Name.Should().Be("file_system");
    }

    [Fact]
    public async Task Get_RegisteredButUngrantedTool_Answers404JustLikeANameThatDoesNotExist()
    {
        // Answering 403 for the registered one would confirm it exists, letting any caller map the
        // host's inventory one name at a time — defeating the filtering on the list endpoint.
        using var factory = FactoryGranting("file_system");
        using var client = factory.CreateClient();

        var registeredButUngranted = await client.GetAsync("/api/tools/document_search");
        var neverRegistered = await client.GetAsync("/api/tools/no_such_tool_exists");

        registeredButUngranted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        neverRegistered.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void EveryCatalogedName_ResolvesToAToolThatAgreesWithIt()
    {
        // THE INVARIANT: a tool's Name and its keyed-DI registration key are assumed equal
        // everywhere in the harness — ToolChainBuilder and ToolRiskClassifier both resolve by name,
        // and CapabilityEnvelope.AllowedTools is a list of names. A tool registered under a key that
        // differs from its own Name is reachable under one identifier and describes itself as
        // another, so a grant naming either one governs only half the paths that matter. Nothing
        // else in the codebase checks this; it is asserted against the real container rather than
        // left to convention.
        using var factory = new WebApplicationFactory<Program>();
        var provider = factory.Services;
        var catalog = provider.GetRequiredService<IToolCatalog>();

        // Grant every key the catalog knows, by asking it for the ones it publishes. Built from the
        // catalog rather than a hand-written list so a newly registered tool is covered the day it
        // lands.
        var everyKey = KeyedToolRegistrationKeys(factory);
        everyKey.Should().NotBeEmpty("the host registers tools; an empty sweep would make this vacuous");

        var listed = catalog.ListGranted(new CapabilityEnvelope { AllowedTools = everyKey });
        listed.Should().NotBeEmpty("at least one registered tool must be constructible in this host");

        using var _ = new FluentAssertions.Execution.AssertionScope();
        foreach (var entry in listed)
        {
            var resolved = provider.GetKeyedService<ITool>(entry.Name);

            resolved.Should().NotBeNull($"catalog entry '{entry.Name}' must resolve by the name it publishes");
            resolved!.Name.Should().Be(
                entry.Name,
                "the tool resolved by this name must agree that it is its name");
        }
    }

    /// <summary>
    /// The keys under which the running host registers tools, read from its own service collection so
    /// the sweep cannot drift from the registrations.
    /// </summary>
    private static IReadOnlyList<string> KeyedToolRegistrationKeys(WebApplicationFactory<Program> factory)
    {
        var keys = new List<string>();

        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            keys.AddRange(services
                .Where(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ITool))
                .Select(descriptor => descriptor.ServiceKey as string)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!))))
            .Services.GetService<IToolCatalog>();

        return keys;
    }

    private sealed class FixedEnvelopeResolver(CapabilityEnvelope envelope) : ICapabilityEnvelopeResolver
    {
        public CapabilityEnvelope Resolve(ClaimsPrincipal? principal) => envelope;
    }
}
