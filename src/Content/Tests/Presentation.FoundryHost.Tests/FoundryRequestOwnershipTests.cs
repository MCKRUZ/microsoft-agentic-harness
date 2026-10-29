using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Azure.AI.AgentServer.Core;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
using Tests.Common;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// #1008: every request this host serves runs as the caller Foundry verified, through the real composition root the host
/// builds, so what the process-lived agent writes to a shared store carries that caller as owner. A request that names a
/// user who cannot be resolved is refused, never run unowned; one that names nobody runs unscoped, as before.
/// </summary>
/// <remarks>
/// The composition is the production one (<c>RegisterConfigSections</c> + <c>BuildGlobalSolutionServices</c>) bound with
/// this host's own bootstrap overrides, so the knowledge scope, its writer and the knowledge memory are the ones the host
/// resolves, not stand-ins. Each check reads from a scope other than the request's, as the agent does.
/// </remarks>
public sealed class FoundryRequestOwnershipTests : IDisposable
{
    private const string FoundryUser = "foundry-user-7f3a";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "foundry-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;

    /// <summary>Composes the host's production service graph with its state rooted in a throwaway <c>$HOME</c>.</summary>
    public FoundryRequestOwnershipTests()
    {
        Directory.CreateDirectory(_home);
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(name => name == "HOME" ? _home : null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides.ToDictionary(o => o.Key.Replace("__", ":"), o => (string?)o.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* a SQLite handle can outlive the provider briefly; the temp folder is not load-bearing */ }
    }

    private static DefaultHttpContext Request(params string[] userIds)
    {
        var context = new DefaultHttpContext();
        if (userIds.Length > 0)
            context.Request.Headers[PlatformHeaders.UserId] = userIds;
        return context;
    }

    /// <summary>The ambient owner the agent sees, read from a scope the request did not open.</summary>
    private string? OwnerSeenByTheAgent()
    {
        using var elsewhere = _provider.CreateScope();
        return elsewhere.ServiceProvider.GetRequiredService<IKnowledgeScope>().UserId;
    }

    [Fact]
    public async Task RunAsync_WithTheVerifiedHeader_RunsTheRequestAsThatUser()
    {
        string? seen = null;

        await FoundryRequestScope.RunAsync(_provider, Request(FoundryUser), () =>
        {
            seen = OwnerSeenByTheAgent();
            return Task.CompletedTask;
        });

        seen.Should().Be(FoundryUser);
        OwnerSeenByTheAgent().Should().BeNull("the owner is the request's, not the process's");
    }

    [Fact]
    public async Task RunAsync_WithTheVerifiedHeader_StampsTheOwnerOnWhatTheAgentWritesToTheKnowledgeGraph()
    {
        IReadOnlyList<Domain.AI.KnowledgeGraph.Models.GraphNode> owned = [];

        await FoundryRequestScope.RunAsync(_provider, Request(FoundryUser), async () =>
        {
            await using var turn = _provider.CreateAsyncScope();
            await turn.ServiceProvider.GetRequiredService<IKnowledgeMemory>()
                .RememberAsync("favourite-colour", "The caller's favourite colour is green.");

            owned = await turn.ServiceProvider.GetRequiredService<IKnowledgeGraphStore>().GetNodesByOwnerAsync(FoundryUser);
        });

        owned.Should().ContainSingle(n => n.Name == "favourite-colour")
            .Which.OwnerId.Should().Be(FoundryUser, "an operator erasure naming this id must find the record");
    }

    [Fact]
    public async Task RunAsync_WithNoHeader_RunsUnscopedAsBefore()
    {
        var ran = false;
        string? seen = "unset";

        await FoundryRequestScope.RunAsync(_provider, Request(), () =>
        {
            ran = true;
            seen = OwnerSeenByTheAgent();
            return Task.CompletedTask;
        });

        ran.Should().BeTrue("a local run or the readiness probe carries no user and is still served");
        seen.Should().BeNull();
    }

    public static TheoryData<string, Action<HttpContext>> OtherIdentityClaims => new()
    {
        { "a pass-through client header", c => c.Request.Headers["x-client-agent-user-id"] = FoundryUser },
        { "a bearer token", c => c.Request.Headers.Authorization = "Bearer eyJhbGciOiJub25lIn0.eyJvaWQiOiJ4In0." },
        {
            "a principal set earlier in the pipeline",
            c => c.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("oid", FoundryUser)], "Earlier"))
        },
    };

    [Theory]
    [MemberData(nameof(OtherIdentityClaims))]
    public async Task RunAsync_IdentityFromAnythingButThePlatformHeader_IsIgnored(string _, Action<HttpContext> claim)
    {
        // Foundry's gateway drops every caller header outside its allowlist, so only the platform's own header is trusted.
        var context = Request();
        claim(context);
        string? seen = "unset";

        await FoundryRequestScope.RunAsync(_provider, context, () =>
        {
            seen = OwnerSeenByTheAgent();
            return Task.CompletedTask;
        });

        seen.Should().BeNull();
    }

    public static TheoryData<string[]> UnusableUserIds => new()
    {
        new[] { FoundryUser, "someone-else" },
        new[] { "" },
        new[] { "   " },
        // Ids an operator erasure would refuse to name, so their records could never be erased.
        new[] { " " + FoundryUser },
        new[] { new string('u', EraseSubjectDataCommandValidator.MaxSubjectIdLength + 1) },
    };

    [Theory]
    [MemberData(nameof(UnusableUserIds))]
    public async Task RunAsync_AHeaderThatNamesNoOneUsable_IsRefusedRatherThanRunUnowned(string[] userIds)
    {
        // A request that claims a user we cannot resolve would otherwise write records with no owner, which read as global.
        var context = Request(userIds);
        var ran = false;

        await FoundryRequestScope.RunAsync(_provider, context, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        ran.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task RunAsync_TheSameIdSentTwice_IsOneIdentity()
    {
        string? seen = null;

        await FoundryRequestScope.RunAsync(_provider, Request(FoundryUser, FoundryUser), () =>
        {
            seen = OwnerSeenByTheAgent();
            return Task.CompletedTask;
        });

        seen.Should().Be(FoundryUser);
    }

    [Fact]
    public async Task RunAsync_TheLongestIdAnErasureAccepts_IsServed()
    {
        var longest = new string('u', EraseSubjectDataCommandValidator.MaxSubjectIdLength);
        string? seen = null;

        await FoundryRequestScope.RunAsync(_provider, Request(longest), () =>
        {
            seen = OwnerSeenByTheAgent();
            return Task.CompletedTask;
        });

        seen.Should().Be(longest);
    }

    [Theory]
    [InlineData("agent.yaml")]
    [InlineData("agent.manifest.yaml")]
    public void TheDeploymentDescriptor_DeclaresContainerProtocol2(string descriptor)
    {
        // Foundry stamps x-agent-user-id only on 2.0.0, the hosting package speaks only 2.0.0, and 1.0.0 is no longer served.
        var yaml = File.ReadAllText(RepoRoot.Combine(
            "src", "Content", "Presentation", "Presentation.FoundryHost", descriptor));

        yaml.Should().MatchRegex(@"(?m)-\s*protocol:\s*responses[ \t]*\r?\n\s*version:\s*[""']?2\.0\.0[""']?[ \t]*(#.*)?\r?$");
        yaml.Should().NotMatchRegex(@"version:\s*[""']?1\.0\.0");
    }

    [Fact]
    public void Main_RunsEveryRequestThroughTheRequestScope_WithTheRequest()
    {
        // The ownership above is only worth anything if Program hands each request, header and all, to the scope.
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(
            RepoRoot.Combine("src", "Content", "Presentation", "Presentation.FoundryHost", "Program.cs")));

        code.Should().MatchRegex(
            @"app\.App\.Use\(\(context,\s*next\)\s*=>\s*FoundryRequestScope\.RunAsync\(provider,\s*context,\s*\(\)\s*=>\s*next\(context\)\)\)");
    }
}
