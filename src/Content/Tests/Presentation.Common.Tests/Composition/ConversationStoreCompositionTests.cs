using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services.AI;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Wiring integration tests for the conversation transcript store (issue #235): through the REAL
/// composition root, <see cref="IConversationStore"/> must resolve for <em>every</em> host, and the
/// implementation behind it must be the one configuration asks for.
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
    private readonly string _workingDir;

    /// <summary>Creates the isolated directory this fixture points both providers at.</summary>
    public ConversationStoreCompositionTests()
    {
        _workingDir = Path.Combine(
            Path.GetTempPath(), "composition-conversations-" + Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Disposing the provider closes the store's connections but returns them to Microsoft.Data
        // .Sqlite's pool, which keeps the file handle open — the database file is then undeletable.
        // Production wants that pooling, so the fixture drains it instead of turning it off. Idle
        // pooled connections are all this closes; connections another test class holds open are
        // untouched.
        SqliteConnection.ClearAllPools();

        // Only the fixture's own directory. A regressed binding leaves a stray "./conversations" in
        // the shared test working directory, but reaching out to delete that would race the other
        // classes in this folder that xUnit runs in parallel — a worse problem than the litter.
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public async Task CompositionRoot_ResolvesConversationStore_ForEveryHost()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(SqliteSettings());

        var store = provider.GetService<IConversationStore>();

        store.Should().NotBeNull(
            "the transcript store is shared infrastructure — a registration only one host can reach "
            + "is the defect issue #235 exists to fix");
    }

    [Fact]
    public async Task CompositionRoot_RegistersTheBudgetDisclosure_ForEveryHost()
    {
        // The disclosure exists so a deployment running without a token ceiling cannot do so quietly
        // (issue #279). Its own unit tests drive it directly, so they stay green with the registration
        // deleted — and an unregistered hosted service is silent, which is the exact failure it was
        // written to end. This is the assertion that makes the registration load-bearing.
        await using var provider = CompositionRootTestHost.BuildProvider(SqliteSettings());

        provider.GetServices<IHostedService>()
            .Should().Contain(s => s.GetType().Name == "ConversationBudgetStartupDisclosure",
                "a disclosure nothing resolves discloses nothing");
    }

    [Fact]
    public async Task CompositionRoot_RegistersConversationStore_AsSingleton()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(SqliteSettings());

        var first = provider.GetRequiredService<IConversationStore>();
        var second = provider.GetRequiredService<IConversationStore>();

        first.Should().BeSameAs(second,
            "the file-backed provider serialises all of its I/O behind one SemaphoreSlim, so a scoped "
            + "or transient registration would hand out several stores with several semaphores and "
            + "silently lose that serialisation; the lifetime must not depend on which provider is live");
    }

    [Fact]
    public async Task CompositionRoot_DefaultProvider_IsTheSqliteStore()
    {
        // The default is the whole point of the switch: a consumer who configures nothing gets the
        // implementation that is safe for more than one host, not the one that is not.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
        });

        provider.GetRequiredService<IConversationStore>()
            .Should().BeOfType<EfCoreConversationStore>();
    }

    [Fact]
    public async Task CompositionRoot_FileSystemProvider_IsSelectedByConfigAndBindsItsPath()
    {
        Directory.Exists(_workingDir).Should().BeFalse("the fixture must start from nothing");

        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:Provider"] = "FileSystem",
            ["AppConfig:AI:Conversations:ConversationsPath"] = _workingDir,
        });

        provider.GetRequiredService<IConversationStore>()
            .Should().BeOfType<FileSystemConversationStore>();

        // The file-backed store resolves its base path and creates the directory during
        // construction, so the directory appearing here is proof the configured path reached it.
        // Binding the wrong section would silently fall back to the "./conversations" default and
        // leave this absent — which, for a transcript store, means every conversation quietly lands
        // somewhere else.
        Directory.Exists(_workingDir).Should().BeTrue(
            "AppConfig:AI:Conversations:ConversationsPath must reach the store; this setting moved "
            + "out of AppConfig:AgentHub when the store became shared infrastructure");
    }

    [Theory]
    [InlineData(
        null,
        typeof(EfCoreConversationStore),
        typeof(SqliteConversationTurnLease),
        typeof(SqliteConversationBudgetTracker))]
    [InlineData(
        "FileSystem",
        typeof(FileSystemConversationStore),
        typeof(InProcessConversationTurnLease),
        typeof(InProcessConversationBudgetTracker))]
    public async Task CompositionRoot_TurnLease_AlwaysMatchesTheSelectedStore(
        string? provider, Type expectedStore, Type expectedLease, Type expectedBudget)
    {
        // This also covers "the lease resolves at all", which matters for the same reason the store
        // does: two hosts can now run turns on one conversation, so what stops them running at the
        // same time has to be reachable from both, not registered by whichever host thought of it.
        //
        // The pairing is load-bearing, not tidiness. The durable lease finds a conversation by
        // reading the same database the durable store writes to, so a host that mixes the two — the
        // file-backed store with the durable lease — has a lease looking for conversations in a
        // database nothing writes to, and refuses every turn. Two AgentHub test factories did exactly
        // that for one test run while this lease was being built.
        //
        // The budget tracker is the third member of the same choice, for the same kind of reason: a
        // conversation whose transcript is shared between hosts but whose token ceiling is not lets
        // each host enforce a private copy of one number, so the conversation spends roughly twice
        // what was configured and nothing reports an error (issue #245).
        var settings = new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Conversations:ConversationsPath"] = _workingDir,
        };

        if (provider is not null)
            settings["AppConfig:AI:Conversations:Provider"] = provider;

        await using var built = CompositionRootTestHost.BuildProvider(settings);

        built.GetRequiredService<IConversationStore>().Should().BeOfType(expectedStore);
        built.GetRequiredService<IConversationTurnLease>().Should().BeOfType(expectedLease);
        built.GetRequiredService<IConversationBudgetTracker>().Should().BeOfType(expectedBudget);
    }

    private Dictionary<string, string?> SqliteSettings() => new()
    {
        ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
    };
}
