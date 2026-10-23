using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>Configuration for planner tests that construct the plan store directly.</summary>
internal static class PlannerTestConfig
{
    /// <summary>A monitor over <paramref name="config"/>, or the default configuration.</summary>
    public static IOptionsMonitor<AppConfig> Monitor(AppConfig? config = null) => new Fixed(config ?? new AppConfig());

    private sealed class Fixed(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => value;

        public AppConfig Get(string? name) => value;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
