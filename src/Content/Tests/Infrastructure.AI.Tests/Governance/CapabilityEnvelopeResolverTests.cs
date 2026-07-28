using System.Security.Claims;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.BundleExecution;
using FluentAssertions;
using Infrastructure.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

/// <summary>
/// Tests <see cref="CapabilityEnvelopeResolver"/> — the config-to-grant mapping. Covers the precedence
/// (exact subject → least-privilege combination of matching roles → default), the fail-closed default for
/// an unmatched caller, safe degradation of an invalid autonomy ceiling, and the invariant that overlapping
/// roles can only ever narrow a grant.
/// </summary>
public sealed class CapabilityEnvelopeResolverTests
{
    private static CapabilityEnvelopeResolver Resolver(
        CapabilityEnvelopesConfig envelopes,
        ILogger<CapabilityEnvelopeResolver>? logger = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new Domain.Common.Config.AI.BundleExecution.BundleExecutionConfig { Envelopes = envelopes }
            }
        };

        return new CapabilityEnvelopeResolver(
            Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig),
            logger ?? NullLogger<CapabilityEnvelopeResolver>.Instance);
    }

    /// <summary>
    /// Counts warnings whose message contains <paramref name="fragment"/>, so an assertion pins the
    /// specific diagnostic rather than "some warning happened".
    /// </summary>
    private static int WarningsContaining(Mock<ILogger<CapabilityEnvelopeResolver>> logger, string fragment) =>
        logger.Invocations
            .Count(i => i.Method.Name == nameof(ILogger.Log)
                        && (LogLevel)i.Arguments[0] == LogLevel.Warning
                        && i.Arguments[2]?.ToString()?.Contains(fragment, StringComparison.Ordinal) == true);

    private static ClaimsPrincipal Principal(string? subject = null, params string[] roles)
    {
        var claims = new List<Claim>();
        if (subject is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void NullPrincipal_ReturnsDefault()
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            Default = new CapabilityEnvelopeConfig { AllowedTools = ["read_only"], AutonomyCeiling = "Supervised" }
        });

        var envelope = resolver.Resolve(null);

        envelope.AllowedTools.Should().BeEquivalentTo(["read_only"]);
        envelope.AutonomyCeiling.Should().Be(AutonomyLevel.Supervised);
    }

    [Fact]
    public void UnmatchedCaller_UnconfiguredDefault_GrantsNothing_AndIsRestricted()
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig());

        var envelope = resolver.Resolve(Principal(subject: "nobody", roles: "stranger"));

        envelope.AllowedTools.Should().BeEmpty("the fail-closed default grants no tools");
        envelope.AllowedMcpServers.Should().BeEmpty();
        envelope.AutonomyCeiling.Should().Be(AutonomyLevel.Restricted);
    }

    [Fact]
    public void SubjectMatch_WinsOverRole()
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            BySubject = { ["alice"] = new CapabilityEnvelopeConfig { AllowedTools = ["subject_tool"], AutonomyCeiling = "Autonomous" } },
            ByRole = { ["admin"] = new CapabilityEnvelopeConfig { AllowedTools = ["role_tool"], AutonomyCeiling = "Restricted" } }
        });

        var envelope = resolver.Resolve(Principal(subject: "alice", roles: "admin"));

        envelope.AllowedTools.Should().BeEquivalentTo(["subject_tool"], "an exact subject grant takes precedence over any role");
        envelope.AutonomyCeiling.Should().Be(AutonomyLevel.Autonomous);
    }

    [Fact]
    public void SubjectKey_MatchedCaseInsensitively()
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            BySubject = { ["Alice"] = new CapabilityEnvelopeConfig { AllowedTools = ["t"] } }
        });

        resolver.Resolve(Principal(subject: "alice")).AllowedTools.Should().BeEquivalentTo(["t"]);
    }

    [Fact]
    public void RoleMatch_UsedWhenNoSubjectGrant()
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            ByRole = { ["reader"] = new CapabilityEnvelopeConfig { AllowedTools = ["doc_search"], AllowedMcpServers = ["kb"], AutonomyCeiling = "Supervised" } }
        });

        var envelope = resolver.Resolve(Principal(subject: "unknown", roles: "reader"));

        envelope.AllowedTools.Should().BeEquivalentTo(["doc_search"]);
        envelope.AllowedMcpServers.Should().BeEquivalentTo(["kb"]);
        envelope.AutonomyCeiling.Should().Be(AutonomyLevel.Supervised);
    }

    [Fact]
    public void MultipleRoles_CombineToLeastPrivilege()
    {
        // Two roles grant overlapping-but-different surfaces at different ceilings. The caller with both
        // gets the INTERSECTION of tools/servers and the MINIMUM ceiling — overlapping roles never widen.
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            ByRole =
            {
                ["writer"] = new CapabilityEnvelopeConfig
                {
                    AllowedTools = ["file_system", "search"], AllowedMcpServers = ["kb", "web"], AutonomyCeiling = "Autonomous"
                },
                ["auditor"] = new CapabilityEnvelopeConfig
                {
                    AllowedTools = ["search", "log_read"], AllowedMcpServers = ["kb"], AutonomyCeiling = "Supervised"
                }
            }
        });

        var envelope = resolver.Resolve(Principal(subject: "u", roles: ["writer", "auditor"]));

        envelope.AllowedTools.Should().BeEquivalentTo(["search"], "only the tool common to both roles survives");
        envelope.AllowedMcpServers.Should().BeEquivalentTo(["kb"]);
        envelope.AutonomyCeiling.Should().Be(AutonomyLevel.Supervised, "the minimum of the two ceilings");
    }

    [Fact]
    public void OnlyOneRoleConfigured_OtherRolesIgnored()
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            ByRole = { ["reader"] = new CapabilityEnvelopeConfig { AllowedTools = ["doc_search"] } }
        });

        // "writer" has no config entry, so only "reader" contributes — no accidental intersection to empty.
        var envelope = resolver.Resolve(Principal(subject: "u", roles: ["reader", "writer"]));

        envelope.AllowedTools.Should().BeEquivalentTo(["doc_search"]);
    }

    [Theory]
    [InlineData("SuperUser")]   // unknown name
    [InlineData("2")]           // numeric — Enum.TryParse would accept this as Autonomous
    [InlineData("Restricted,Autonomous")] // comma-composite — Enum.TryParse would OR to Autonomous
    [InlineData("")]            // empty
    public void NonTierNameCeiling_FallsBackToRestricted_NeverWidens(string ceiling)
    {
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            Default = new CapabilityEnvelopeConfig { AllowedTools = ["t"], AutonomyCeiling = ceiling }
        });

        resolver.Resolve(null).AutonomyCeiling.Should().Be(AutonomyLevel.Restricted,
            "only an exact tier name is accepted; anything else degrades closed rather than silently granting autonomy");
    }

    [Fact]
    public void McpServersGrantedButNoTools_Warns_AndStillResolves()
    {
        // AllowedMcpServers gates which servers may be CONTACTED; AllowedTools gates which tools may be
        // INVOKED. Granting servers while naming no tools makes the run fetch and publish every one of
        // their schemas and then deny every call — a shape that is almost certainly operator error, and
        // one the shipped onboarding example used to encourage. Warn, never throw: it fails closed, and
        // an empty tool list is legitimate for a caller meant to have no tool access.
        var logger = new Mock<ILogger<CapabilityEnvelopeResolver>>();
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            Default = new CapabilityEnvelopeConfig
            {
                AllowedTools = [],
                AllowedMcpServers = ["internal-docs"],
                AutonomyCeiling = "Autonomous"
            }
        }, logger.Object);

        var envelope = resolver.Resolve(null);

        envelope.AllowedMcpServers.Should().BeEquivalentTo(["internal-docs"],
            "the misconfiguration is reported, not silently corrected");
        WarningsContaining(logger, "internal-docs").Should().Be(1,
            "the operator must be told which servers were granted with no invocable tools");
    }

    [Theory]
    [InlineData(new string[0], new string[0])]                    // grants nothing at all — the fail-closed default
    [InlineData(new[] { "doc_search" }, new[] { "internal-docs" })] // servers AND their tools named — correct
    [InlineData(new[] { "file_system" }, new string[0])]           // tools only, no MCP — nothing to warn about
    public void UsableOrEmptyEnvelope_DoesNotWarn(string[] tools, string[] servers)
    {
        var logger = new Mock<ILogger<CapabilityEnvelopeResolver>>();
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            Default = new CapabilityEnvelopeConfig
            {
                AllowedTools = [.. tools],
                AllowedMcpServers = [.. servers],
                AutonomyCeiling = "Autonomous"
            }
        }, logger.Object);

        resolver.Resolve(null);

        WarningsContaining(logger, "AllowedMcpServers gates").Should().Be(0,
            "the warning is scoped to the unusable shape and must not become background noise");
    }

    [Fact]
    public void Role_FromShortRolesClaim_IsMatched()
    {
        // Hosts whose token pipeline emits a short "roles" claim (e.g. Azure AD app roles) instead of the
        // long SOAP ClaimTypes.Role URI must still resolve their ByRole grant — otherwise role config is dead.
        var resolver = Resolver(new CapabilityEnvelopesConfig
        {
            ByRole = { ["reader"] = new CapabilityEnvelopeConfig { AllowedTools = ["doc_search"] } }
        });

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "reader")], "test"));

        resolver.Resolve(principal).AllowedTools.Should().BeEquivalentTo(["doc_search"]);
    }
}
