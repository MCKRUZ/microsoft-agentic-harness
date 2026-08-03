using Application.AI.Common.Interfaces.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Wiring integration tests for the conversation transcript store (issue #235, PR 1): through the
/// REAL composition root, <see cref="IConversationStore"/> must resolve for <em>every</em> host, not
/// just the one that used to own its registration.
/// </summary>
/// <remarks>
/// <para>
/// Before this change the registration lived in <c>Presentation.AgentHub/DependencyInjection.cs</c>.
/// Peer Presentation projects do not reference one another, so <c>Presentation.ExecutionApi</c> could
/// not reach it — the harness had a complete, mature conversation store that only its interactive
/// entry point could use. Moving the registration into <c>Infrastructure.AI</c> is the entire point
/// of the change, and nothing else asserts it.
/// </para>
/// <para>
/// <strong>Why this cannot be proved by the AgentHub test suites.</strong> Both
/// <c>TestWebApplicationFactory</c> and <c>IntegrationTestFactory</c> register their own
/// <see cref="IConversationStore"/> against a temp directory, so they resolve the store whether or
/// not the production registration exists. They would stay green if the registration were deleted
/// outright. This test builds the shared composition and nothing else, so it fails the moment
/// <c>RegisterConversationStore</c> stops being reachable from a host.
/// </para>
/// </remarks>
public sealed class ConversationStoreCompositionTests : IDisposable
{
    private readonly string _conversationsDir;

    /// <summary>Creates the isolated conversations directory this fixture points the store at.</summary>
    public ConversationStoreCompositionTests()
    {
        _conversationsDir = Path.Combine(
            Path.GetTempPath(), "composition-conversations-" + Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_conversationsDir))
            Directory.Delete(_conversationsDir, recursive: true);
    }

    [Fact]
    public void CompositionRoot_ResolvesConversationStore_ForEveryHost()
    {
        using var provider = CompositionRootTestHost.BuildProvider(Settings());

        var store = provider.GetService<IConversationStore>();

        store.Should().NotBeNull(
            "the transcript store is shared infrastructure — a registration only one host can reach "
            + "is the defect issue #235 exists to fix");
    }

    [Fact]
    public void CompositionRoot_RegistersConversationStore_AsSingleton()
    {
        using var provider = CompositionRootTestHost.BuildProvider(Settings());

        var first = provider.GetRequiredService<IConversationStore>();
        var second = provider.GetRequiredService<IConversationStore>();

        first.Should().BeSameAs(second,
            "FileSystemConversationStore serialises all of its file I/O behind one SemaphoreSlim; a "
            + "scoped or transient registration would hand out several stores with several semaphores "
            + "and silently lose that serialisation");
    }

    [Fact]
    public void CompositionRoot_BindsConversationsPath_FromTheSharedAiSection()
    {
        Directory.Exists(_conversationsDir).Should().BeFalse("the fixture must start from nothing");

        using var provider = CompositionRootTestHost.BuildProvider(Settings());
        _ = provider.GetRequiredService<IConversationStore>();

        // The store resolves its base path and creates the directory during construction, so the
        // directory appearing here is proof the configured path reached it. Binding the wrong
        // section would silently fall back to the "./conversations" default and leave this absent —
        // which, for a transcript store, means every conversation quietly lands somewhere else.
        Directory.Exists(_conversationsDir).Should().BeTrue(
            "AppConfig:AI:Conversations:ConversationsPath must reach the store; this setting moved "
            + "out of AppConfig:AgentHub when the store became shared infrastructure");
    }

    private Dictionary<string, string?> Settings() => new()
    {
        ["AppConfig:AI:Conversations:ConversationsPath"] = _conversationsDir,
    };
}
