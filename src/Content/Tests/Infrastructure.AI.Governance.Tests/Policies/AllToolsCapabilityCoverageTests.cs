using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Connectors;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Infrastructure.AI.Tools.Iac;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Policies;

/// <summary>
/// Guard test for #406: <see cref="DefaultPolicyCapabilityAlignmentTests"/> only proves that every
/// tool <em>named in <c>default-policy.yaml</c>'s three rules</em> has a <c>RequiredCapabilities</c>
/// declaration consistent with its risk tier — 10 tools at the time this landed. It says nothing about
/// the other 15+ registered tools: a new <see cref="ITool"/> implementation that performs real
/// filesystem/network/subprocess/database work but forgets to override
/// <see cref="ITool.RequiredCapabilities"/> compiles cleanly, resolves <see cref="ToolCapability.None"/>,
/// and is silently under-classified — <c>CapabilityEnforcer</c>/<c>ToolPermissionProfileResolver</c>
/// then treat it as needing nothing, and no test whose coverage is keyed off the YAML's own rule
/// conditions ever sees it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Assembly reflection, not DI resolution.</strong> A DI-container sweep (mirroring
/// <c>KeyedServiceScopeSweepTests</c>) was tried first and rejected: tools registered by
/// <c>Infrastructure.AI</c>'s private <c>RegisterToolServices</c> aren't reachable in isolation (that
/// test's own coverage-boundary note says so), and even a full <c>Presentation.AgentHub</c> host build
/// throws on unrelated missing configuration (<c>AppConfig.AI.GitOps.ActiveController</c>) before every
/// tool can be constructed — a config-completeness dependency this test has no business carrying.
/// Reflection sidesteps both: every <see cref="ITool.RequiredCapabilities"/> implementation in this
/// codebase is a trivial expression-bodied property or a reference to a <c>public const</c> field (all
/// 16 explicit overrides checked directly), never touching an injected field, so an instance built via
/// <see cref="RuntimeHelpers.GetUninitializedObject"/> — bypassing every constructor and its
/// dependencies entirely — is safe to read that one property from.
/// </para>
/// </remarks>
public sealed class AllToolsCapabilityCoverageTests
{
    /// <summary>
    /// Tool types whose <see cref="ITool.RequiredCapabilities"/> is deliberately
    /// <see cref="ToolCapability.None"/> — a reviewed, justified allowlist, not a default a new tool
    /// falls into silently. A registered tool whose type is neither in this set nor declares a
    /// non-<see cref="ToolCapability.None"/> requirement fails
    /// <see cref="EveryConcreteTool_DeclaresCapabilitiesDeliberately"/> until a human adds it here with
    /// a reason, or gives it a real declaration.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> IntentionallyNoCapabilities = new Dictionary<Type, string>
    {
        [typeof(EchoLookupTool)] = "Deterministic E2E test tool — returns canned data, touches nothing external.",
        [typeof(EchoCalculateTool)] = "Deterministic E2E test tool — returns canned data, touches nothing external.",
        [typeof(ListMetricsTool)] = "Reads the in-process IMetricCatalog seam — no filesystem, subprocess, or network I/O.",
        [typeof(IacGenerateTool)] = "Scaffolds files in memory and returns them as JSON — never writes to disk or deploys.",
        // The whole BlockingProxyTool family (#387): the entire effect is an in-process AG-UI round
        // trip to a browser already attached to this run — no filesystem, subprocess, or new outbound
        // network connection this tool itself initiates. See BlockingProxyTool's own remarks.
        [typeof(DashboardControlTool)] = "BlockingProxyTool family — in-process AG-UI round trip only.",
        [typeof(RenderChartTool)] = "BlockingProxyTool family — in-process AG-UI round trip only.",
        [typeof(RenderImageTool)] = "BlockingProxyTool family — in-process AG-UI round trip only.",
        [typeof(RenderFormTool)] = "BlockingProxyTool family — in-process AG-UI round trip only.",
        [typeof(RenderTableTool)] = "BlockingProxyTool family — in-process AG-UI round trip only.",
    };

    [Fact]
    public void EveryConcreteTool_DeclaresCapabilitiesDeliberately()
    {
        var toolTypes = ConcreteToolTypes().ToList();
        toolTypes.Should().NotBeEmpty("the assembly scan must find at least the tools this file lists");
        toolTypes.Should().Contain(typeof(FileSystemTool),
            "the scan must reach production tools, not just an empty or filtered set");

        using var _ = new FluentAssertions.Execution.AssertionScope();
        foreach (var type in toolTypes)
        {
            // Bypasses every constructor and its dependencies — safe only because every
            // RequiredCapabilities implementation in this codebase is a constant expression; see this
            // class's remarks.
            var instance = (ITool)RuntimeHelpers.GetUninitializedObject(type);

            if (instance.RequiredCapabilities != ToolCapability.None)
                continue;

            IntentionallyNoCapabilities.Should().ContainKey(type,
                $"'{type.Name}' declares ToolCapability.None with no reviewed justification on the " +
                "allowlist — either give it a real RequiredCapabilities declaration, or add it to " +
                "IntentionallyNoCapabilities with a one-line reason");
        }
    }

    /// <summary>
    /// Every non-abstract type assignable to <see cref="ITool"/> in the two assemblies first-party
    /// tools live in. Abstract bases (<c>BlockingProxyTool</c>, <c>SingleRenderProxyTool</c>) are
    /// excluded by construction — nothing is ever registered under them.
    /// </summary>
    private static IEnumerable<Type> ConcreteToolTypes() =>
        new[] { typeof(FileSystemTool).Assembly, typeof(ConnectorToolAdapter).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t))
            .Distinct();
}
