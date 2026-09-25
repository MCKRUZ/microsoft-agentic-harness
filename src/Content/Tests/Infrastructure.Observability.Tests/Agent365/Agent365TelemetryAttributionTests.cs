using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Agent365;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Observability.Tests.Agent365;

/// <summary>
/// Tests for <see cref="Agent365TelemetryAttribution"/>, which publishes the running agent's Entra
/// agent identity for the duration of a turn.
/// </summary>
/// <remarks>
/// <para>
/// Assertions are made against the baggage the vendor SDK actually publishes, read back through
/// <see cref="OpenTelemetry.Baggage"/>. That is deliberate rather than incidental: the exporter
/// identifies a span by these values and silently drops any span carrying neither, so a test that
/// only checked "a scope was returned" would pass while publishing nothing at all — the exact failure
/// this class exists to prevent.
/// </para>
/// <para>
/// Each test clears baggage first. It is ambient and flows with async context, so a value left behind
/// by a previous test would make a later assertion pass for the wrong reason.
/// </para>
/// </remarks>
public class Agent365TelemetryAttributionTests
{
    private const string HostAppId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string HostBlueprint = "33333333-3333-3333-3333-333333333333";
    private const string OverrideAppId = "44444444-4444-4444-4444-444444444444";
    private const string OverrideBlueprint = "55555555-5555-5555-5555-555555555555";

    private static Agent365TelemetryAttribution Build(Action<Agent365ExporterConfig> configure)
    {
        OpenTelemetry.Baggage.ClearBaggage();

        var appConfig = new AppConfig();
        configure(appConfig.Observability.Exporters.Agent365);

        return new Agent365TelemetryAttribution(
            new StaticOptionsMonitor(appConfig),
            NullLogger<Agent365TelemetryAttribution>.Instance);
    }

    private static string? Baggage(string key) => OpenTelemetry.Baggage.GetBaggage(key);

    private static IReadOnlyDictionary<string, string> AllBaggage()
        => OpenTelemetry.Baggage.GetBaggage();

    [Theory]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111-1111-1111-1111-111111111111 ")]
    [InlineData("11111111111111111111111111111111")]
    public void NonCanonicalIds_ArePublishedInCanonicalForm(string appId)
    {
        // The validator accepts any form Guid.TryParse does, and trims — so a braced id copied from the
        // portal, or one with a trailing space, passes startup. Published verbatim, the service would
        // find it did not match the authenticated caller and drop every span WITHOUT reporting an
        // error, which is the exact failure the validator exists to prevent. What is checked and what
        // is published have to be the same value.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = appId;
            c.TenantId = TenantId;
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().Contain(HostAppId);
        AllBaggage().Values.Should().NotContain(appId);
    }

    [Fact]
    public void Disabled_PublishesNothing()
    {
        var attribution = Build(c => c.Enabled = false);

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Should().BeEmpty(
            "a host that has not enabled Agent 365 must publish no attribution at all");
    }

    [Fact]
    public void EnabledWithHostDefault_PublishesTheAgentAndTenant()
    {
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        // Asserted by value, not merely by presence: the exporter validates the agent id in the
        // payload against the authenticated caller, so publishing the wrong one fails as surely as
        // publishing none.
        AllBaggage().Values.Should().Contain(HostAppId);
        AllBaggage().Values.Should().Contain(TenantId);
        AllBaggage().Values.Should().Contain("conv-1");
    }

    [Fact]
    public void EnabledWithNoIdentityConfigured_PublishesNothing()
    {
        // Enabled but unconfigured must not fall back to publishing a partial identity: a span with a
        // tenant and no agent is dropped anyway, and inventing one would mis-attribute the activity.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = null;
            c.TenantId = TenantId;
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Should().BeEmpty();
    }

    [Fact]
    public void PerAgentOverride_WinsOverTheHostDefault()
    {
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.Agents["researcher"] = new Agent365AgentIdentityConfig { AppId = OverrideAppId };
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().Contain(OverrideAppId);
        AllBaggage().Values.Should().NotContain(
            HostAppId,
            "an agent with its own identity must not also report the host default, or the tenant's "
            + "inventory attributes its activity to the wrong agent");
    }

    [Fact]
    public void PerAgentOverride_IsMatchedCaseInsensitively()
    {
        // The key is a human-typed agent name in configuration; matching it case-sensitively would
        // fall through to the host default silently, which is the failure this guards.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.Agents["Researcher"] = new Agent365AgentIdentityConfig { AppId = OverrideAppId };
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().Contain(OverrideAppId);
    }

    [Fact]
    public void UnmatchedAgent_FallsBackToTheHostDefault()
    {
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.Agents["summariser"] = new Agent365AgentIdentityConfig { AppId = OverrideAppId };
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().Contain(HostAppId);
        AllBaggage().Values.Should().NotContain(OverrideAppId);
    }

    [Fact]
    public void PerAgentOverrideWithoutABlueprint_DoesNotInheritTheHostBlueprint()
    {
        // A blueprint identifies a KIND of agent. An agent minted from a different blueprint that
        // inherited the host's would be filed under the wrong kind in the tenant's inventory —
        // worse than reporting no blueprint at all.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.BlueprintId = HostBlueprint;
            c.Agents["researcher"] = new Agent365AgentIdentityConfig { AppId = OverrideAppId };
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().NotContain(HostBlueprint);
    }

    [Fact]
    public void PerAgentOverrideWithItsOwnBlueprint_PublishesThatBlueprint()
    {
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.BlueprintId = HostBlueprint;
            c.Agents["researcher"] = new Agent365AgentIdentityConfig
            {
                AppId = OverrideAppId,
                BlueprintId = OverrideBlueprint,
            };
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().Contain(OverrideBlueprint);
        AllBaggage().Values.Should().NotContain(HostBlueprint);
    }

    [Fact]
    public void HostLevelAgentName_AppliesToTheHostDefaultAgent()
    {
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.AgentName = "Primary Agent";
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().Contain("Primary Agent");
    }

    [Fact]
    public void HostLevelAgentName_IsNotAppliedToAnAgentWithItsOwnIdentity()
    {
        // The host-level name names the host's default agent. Applying it to an agent reporting its
        // own identity collapses every agent in a multi-agent host to one display name while their ids
        // stay distinct — harder to read in the tenant's inventory than no custom name at all.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.AgentName = "Primary Agent";
            c.Agents["researcher"] = new Agent365AgentIdentityConfig { AppId = OverrideAppId };
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().NotContain("Primary Agent");
        AllBaggage().Values.Should().Contain("researcher", "the agent reports its own name instead");
    }

    [Fact]
    public void BlankAgentName_FallsBackToTheAgentIdRatherThanPublishingEmpty()
    {
        // Same template-placeholder reasoning as the blueprint id: "AgentName": "" means "not
        // provided", so it must fall back to the agent's own id instead of publishing an empty
        // display name. This sat two lines from the blueprint guard and was missed when that was fixed.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.AgentName = "";
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().NotContain("");
        AllBaggage().Values.Should().Contain("researcher");
    }

    [Fact]
    public void BlankBlueprintId_IsOmittedRatherThanPublishedEmpty()
    {
        // The validator treats a blank blueprint id as absent so a copied template placeholder does not
        // refuse a boot. The publisher has to agree, or that same placeholder is published as an empty
        // blueprint instead of being left out.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
            c.BlueprintId = "";
        });

        using var scope = attribution.BeginTurn("researcher", "conv-1");

        AllBaggage().Values.Should().NotContain("");
        AllBaggage().Values.Should().Contain(HostAppId, "the agent identity is still published");
    }

    [Fact]
    public void BlankConversationId_IsOmittedRatherThanPublishedEmpty()
    {
        // Conversation id is the exporter's primary key for grouping a run's spans into a session.
        // Publishing it empty would supply a meaningless join key rather than leaving it absent.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
        });

        using var scope = attribution.BeginTurn("researcher", "   ");

        AllBaggage().Values.Should().NotContain("   ");
        AllBaggage().Values.Should().Contain(HostAppId, "the agent identity is still published");
    }

    [Fact]
    public void DisposingTheScope_RemovesTheAttribution()
    {
        // The scope must not leak past the turn. A long-lived worker that kept one turn's attribution
        // ambient would file every later turn — including a different agent's — under it.
        var attribution = Build(c =>
        {
            c.Enabled = true;
            c.AgentAppId = HostAppId;
            c.TenantId = TenantId;
        });

        using (attribution.BeginTurn("researcher", "conv-1"))
        {
            AllBaggage().Should().NotBeEmpty();
        }

        AllBaggage().Should().BeEmpty("attribution must not outlive the turn that published it");
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public StaticOptionsMonitor(AppConfig value) => CurrentValue = value;

        public AppConfig CurrentValue { get; }

        public AppConfig Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<AppConfig, string?> listener) => new NoOp();

        private sealed class NoOp : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
