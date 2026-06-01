using Application.AI.Common.Evaluation.Interfaces;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Evaluation;
using Infrastructure.AI.Evaluation.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Persistence;

public sealed class EvalDashboardDependencyInjectionTests
{
    [Fact]
    public void AddEvalDashboardPersistence_disabled_registers_null_store()
    {
        var services = new ServiceCollection();
        services.AddEvalDashboardPersistence(new EvalDashboardOptions { PersistenceEnabled = false });

        using var provider = services.BuildServiceProvider();

        provider.GetService<IEvalRunStore>().Should().BeOfType<NullEvalRunStore>();
        provider.GetService<IDbContextFactory<EvalDashboardDbContext>>().Should().BeNull();
    }

    [Fact]
    public void AddEvalDashboardPersistence_enabled_registers_store_and_factory()
    {
        var services = new ServiceCollection();
        services.AddEvalDashboardPersistence(new EvalDashboardOptions
        {
            PersistenceEnabled = true,
            ConnectionString = $"Data Source=file:eval-di-{Guid.NewGuid():N}?mode=memory&cache=shared",
        });

        using var provider = services.BuildServiceProvider();

        provider.GetService<IDbContextFactory<EvalDashboardDbContext>>().Should().NotBeNull();
        provider.GetService<IEvalRunStore>().Should().BeOfType<EfCoreEvalRunStore>();
    }

    [Fact]
    public void AddEvalDashboardPersistence_rejects_null_options()
    {
        var services = new ServiceCollection();
        var act = () => services.AddEvalDashboardPersistence(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
