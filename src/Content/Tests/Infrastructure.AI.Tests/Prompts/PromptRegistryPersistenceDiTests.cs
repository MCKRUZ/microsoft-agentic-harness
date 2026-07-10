using Application.AI.Common.Prompts;
using Application.AI.Common.Prompts.Interfaces;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Infrastructure.AI.Prompts.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

public sealed class PromptRegistryPersistenceDiTests
{
    [Fact]
    public void Persistence_disabled_registers_Otel_recorder_and_NoOp_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        services.AddPromptRegistry(
            promptsRootPath: Path.GetTempPath(),
            usageOptions: new PromptUsageOptions { PersistenceEnabled = false });

        using var provider = services.BuildServiceProvider();
        var recorder = provider.GetRequiredService<IPromptUsageRecorder>();

        recorder.Should().BeOfType<OtelPromptUsageRecorder>();
        // A No-op store is registered even with persistence off, so the globally-scanned MediatR
        // handlers that query prompt usage stay constructible under ValidateOnBuild (audit H2).
        // It returns empty results (no rows persisted) and needs no DbContext.
        provider.GetService<IPromptUsageStore>().Should().BeOfType<NullPromptUsageStore>();
        provider.GetService<IDbContextFactory<PromptUsageDbContext>>().Should().BeNull();
    }

    [Fact]
    public void Persistence_enabled_registers_composite_recorder_with_dbcontext()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        var dbPath = Path.Combine(Path.GetTempPath(), $"prompt-usage-{Guid.NewGuid():N}.db");
        try
        {
            services.AddPromptRegistry(
                promptsRootPath: Path.GetTempPath(),
                usageOptions: new PromptUsageOptions
                {
                    PersistenceEnabled = true,
                    ConnectionString = $"Data Source={dbPath}",
                });

            using var provider = services.BuildServiceProvider();
            var recorder = provider.GetRequiredService<IPromptUsageRecorder>();

            recorder.Should().BeOfType<CompositePromptUsageRecorder>();
            provider.GetService<IPromptUsageStore>().Should().NotBeNull();
            provider.GetService<IDbContextFactory<PromptUsageDbContext>>().Should().NotBeNull();
        }
        finally
        {
            // Best-effort cleanup; the file may be held by SQLite briefly.
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}
