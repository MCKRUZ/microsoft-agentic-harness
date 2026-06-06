using Application.AI.Common.Interfaces.Identity;
using Domain.AI.Identity;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="EntraAgentIdResolver"/> — hierarchy ordering, empty
/// registration, Development-env gating, and aggregate failure reporting.
/// </summary>
public sealed class EntraAgentIdResolverTests
{
    private static IHostEnvironment EnvOf(string envName)
        => Mock.Of<IHostEnvironment>(e => e.EnvironmentName == envName);

    private static Mock<IAgentCredentialProvider> ProviderFor(
        AgentIdentityKind kind,
        Result<AgentIdentity> result)
    {
        var mock = new Mock<IAgentCredentialProvider>();
        mock.SetupGet(p => p.Kind).Returns(kind);
        mock.Setup(p => p.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static EntraAgentIdResolver Build(
        IEnumerable<IAgentCredentialProvider> providers,
        string envName = "Development")
        => new(providers, EnvOf(envName), NullLogger<EntraAgentIdResolver>.Instance);

    private static AgentIdentity Identity(string id, AgentIdentityKind kind)
        => new() { Id = id, Kind = kind };

    [Fact]
    public async Task ResolveAsync_NoProvidersRegistered_ReturnsNoProvidersCode()
    {
        var resolver = Build(Array.Empty<IAgentCredentialProvider>());

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Be(EntraAgentIdResolver.NoProvidersRegisteredCode);
    }

    [Fact]
    public async Task ResolveAsync_SingleProviderSucceeds_ReturnsIdentity()
    {
        var identity = Identity("mi-agent", AgentIdentityKind.ManagedIdentity);
        var provider = ProviderFor(AgentIdentityKind.ManagedIdentity, Result<AgentIdentity>.Success(identity));
        var resolver = Build([provider.Object]);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(identity);
    }

    [Fact]
    public async Task ResolveAsync_FederatedAndManagedRegistered_FederatedTriedFirst()
    {
        var federated = ProviderFor(
            AgentIdentityKind.FederatedCredential,
            Result<AgentIdentity>.Success(Identity("fed-agent", AgentIdentityKind.FederatedCredential)));
        var managed = ProviderFor(
            AgentIdentityKind.ManagedIdentity,
            Result<AgentIdentity>.Success(Identity("mi-agent", AgentIdentityKind.ManagedIdentity)));

        var resolver = Build([managed.Object, federated.Object]); // intentionally out-of-order registration

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("fed-agent");

        managed.Verify(
            p => p.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_FederatedFailsThenManagedSucceeds_ReturnsManaged()
    {
        var federated = ProviderFor(
            AgentIdentityKind.FederatedCredential,
            Result<AgentIdentity>.Fail("agent_identity.federated_unavailable"));
        var managed = ProviderFor(
            AgentIdentityKind.ManagedIdentity,
            Result<AgentIdentity>.Success(Identity("mi-agent", AgentIdentityKind.ManagedIdentity)));

        var resolver = Build([federated.Object, managed.Object]);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("mi-agent");
    }

    [Fact]
    public async Task ResolveAsync_AllProvidersFail_ReturnsAggregateFailureWithAllAttempts()
    {
        var federated = ProviderFor(
            AgentIdentityKind.FederatedCredential,
            Result<AgentIdentity>.Fail("agent_identity.federated_unavailable"));
        var managed = ProviderFor(
            AgentIdentityKind.ManagedIdentity,
            Result<AgentIdentity>.Fail("agent_identity.managed_identity_unavailable"));

        var resolver = Build([federated.Object, managed.Object]);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        var error = result.Errors.Single();
        error.Should().Contain(EntraAgentIdResolver.NoProviderSucceededCode);
        error.Should().Contain("FederatedCredential");
        error.Should().Contain("agent_identity.federated_unavailable");
        error.Should().Contain("ManagedIdentity");
        error.Should().Contain("agent_identity.managed_identity_unavailable");
    }

    [Fact]
    public async Task ResolveAsync_UnspecifiedKindProvider_IsSkipped()
    {
        var bogus = ProviderFor(
            AgentIdentityKind.Unspecified,
            Result<AgentIdentity>.Success(Identity("ghost", AgentIdentityKind.Unspecified)));
        var managed = ProviderFor(
            AgentIdentityKind.ManagedIdentity,
            Result<AgentIdentity>.Success(Identity("mi-agent", AgentIdentityKind.ManagedIdentity)));

        var resolver = Build([bogus.Object, managed.Object]);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("mi-agent");
        bogus.Verify(
            p => p.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DevelopmentKindInProduction_IsSkipped()
    {
        var dev = ProviderFor(
            AgentIdentityKind.Development,
            Result<AgentIdentity>.Success(Identity("dev-agent", AgentIdentityKind.Development)));

        var resolver = Build([dev.Object], envName: Environments.Production);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        // No other providers → final failure
        result.IsSuccess.Should().BeFalse();

        // Dev provider was NOT consulted at all (short-circuit in the resolver)
        dev.Verify(
            p => p.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DevelopmentKindInDevelopment_IsHonoured()
    {
        var dev = ProviderFor(
            AgentIdentityKind.Development,
            Result<AgentIdentity>.Success(Identity("dev-agent", AgentIdentityKind.Development)));

        var resolver = Build([dev.Object], envName: Environments.Development);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("dev-agent");
    }

    [Fact]
    public async Task ResolveAsync_DevelopmentTriedLast_FallbackWorksInDevelopment()
    {
        // Federated fails, managed not registered, certificate fails, client secret fails,
        // development succeeds. Walk the full hierarchy.
        var federated = ProviderFor(
            AgentIdentityKind.FederatedCredential,
            Result<AgentIdentity>.Fail("federated_unavailable"));
        var certificate = ProviderFor(
            AgentIdentityKind.Certificate,
            Result<AgentIdentity>.Fail("certificate_unavailable"));
        var clientSecret = ProviderFor(
            AgentIdentityKind.ClientSecret,
            Result<AgentIdentity>.Fail("client_secret_unavailable"));
        var dev = ProviderFor(
            AgentIdentityKind.Development,
            Result<AgentIdentity>.Success(Identity("dev-agent", AgentIdentityKind.Development)));

        var resolver = Build(
            [federated.Object, certificate.Object, clientSecret.Object, dev.Object],
            envName: Environments.Development);

        var result = await resolver.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("dev-agent");
    }

    [Fact]
    public async Task ResolveAsync_NullContext_ThrowsArgumentNull()
    {
        var resolver = Build([
            ProviderFor(AgentIdentityKind.ManagedIdentity,
                Result<AgentIdentity>.Success(Identity("mi-agent", AgentIdentityKind.ManagedIdentity))).Object
        ]);

        var act = () => resolver.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_DuplicateKindRegistration_ThrowsInvalidOperation()
    {
        var first = ProviderFor(
            AgentIdentityKind.ManagedIdentity,
            Result<AgentIdentity>.Success(Identity("mi-one", AgentIdentityKind.ManagedIdentity)));
        var second = ProviderFor(
            AgentIdentityKind.ManagedIdentity,
            Result<AgentIdentity>.Success(Identity("mi-two", AgentIdentityKind.ManagedIdentity)));

        var act = () => Build([first.Object, second.Object]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Multiple IAgentCredentialProvider*ManagedIdentity*");
    }

    [Fact]
    public async Task ResolveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var federated = ProviderFor(
            AgentIdentityKind.FederatedCredential,
            Result<AgentIdentity>.Fail("federated_unavailable"));

        var resolver = Build([federated.Object]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => resolver.ResolveAsync(new CredentialContext { Audience = "api://x" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
