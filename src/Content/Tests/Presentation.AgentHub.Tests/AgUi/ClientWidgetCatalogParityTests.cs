using System.Text.Json;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Domain.Common.Config;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.AgUi;
using Tests.Common;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Binds the three declarations of the WebUI widget catalogue together (#342): the render tools the real
/// composition root registers, the names <see cref="ClientWidgetCatalog"/> persists, and the shared
/// <c>widgetCatalog.json</c> the browser's widget registry is typed from.
/// </summary>
/// <remarks>
/// Drift between them fails silently at runtime. A widget the browser renders but this host does not
/// persist vanishes on reload; one this host persists but the browser cannot render is dropped from the
/// transcript. Neither breaks a build, so this test is what catches them.
/// </remarks>
public sealed class ClientWidgetCatalogParityTests : IClassFixture<TestWebApplicationFactory>
{
    /// <summary>
    /// Render tools that are deliberately not WebUI widgets, and why. Exact: a tool leaving this list must
    /// be removed from it, and a new render tool must either join the catalogue or be named here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotPersistedWidgets = new Dictionary<string, string>
    {
        [RenderChartTool.ToolName] =
            "its client is the Dashboard, whose reload path does not re-render saved widgets, so saving its " +
            "calls would leave empty messages there",
    };

    private readonly TestWebApplicationFactory _factory;

    public ClientWidgetCatalogParityTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void Widgets_MatchTheSharedWebUiCatalogue_NamesAndVersions()
    {
        var shared = ReadSharedCatalogue();

        new ClientWidgetCatalog().Widgets.Should().BeEquivalentTo(shared,
            "the host must save exactly the widgets the browser can render, stamped with the version the " +
            "browser expects; widgetCatalog.json is the one place they are declared");
    }

    [Fact]
    public void RegisteredRenderTools_AreTheCatalogueWidgetsPlusTheNamedExclusions()
    {
        // A GitOps controller is named so the GitOps tools build; every tool must build, or a render tool
        // among the unbuilt ones would go unchecked.
        // Set on the options, not through configuration: AgentHub reads its configuration before a test
        // factory's configuration callbacks apply.
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.PostConfigure<AppConfig>(c => c.AI.GitOps.ActiveController = "flux")));
        var provider = factory.Services;
        var renderTools = new List<string>();
        var unbuilt = new List<string>();
        foreach (var key in provider.GetRequiredService<FirstPartyToolLookup>().RegisteredFirstPartyToolKeys)
        {
            try
            {
                if (KeyedToolResolution.Resolve(provider, key) is SingleRenderProxyTool tool)
                    renderTools.Add(tool.Name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                unbuilt.Add($"{key}: {ex.Message}");
            }
        }

        unbuilt.Should().BeEmpty("a tool this host cannot build could be an unchecked render tool");
        renderTools.Should().BeEquivalentTo(
            ReadSharedCatalogue().Keys.Concat(NotPersistedWidgets.Keys),
            "every registered render tool is either a WebUI widget in the shared catalogue or named in " +
            "NotPersistedWidgets with its reason; a new render tool left out of both would render live " +
            "and vanish on reload");
    }

    private static Dictionary<string, int> ReadSharedCatalogue()
    {
        var path = RepoRoot.Combine(
            "src", "Content", "Presentation", "Presentation.WebUI", "src", "features", "chat", "widgets",
            "widgetCatalog.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("widgets").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
    }
}
