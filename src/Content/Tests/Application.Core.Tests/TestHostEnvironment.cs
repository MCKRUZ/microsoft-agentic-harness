using Microsoft.Extensions.Hosting;
using Moq;

namespace Application.Core.Tests;

/// <summary>A host environment with a fixed application name, for the compliance paths that stamp the host (#1003).</summary>
internal static class TestHostEnvironment
{
    /// <summary>The application name every <see cref="Instance"/> reports.</summary>
    public const string Name = "Test.ComplianceHost";

    /// <summary>A new host environment named <see cref="Name"/>.</summary>
    public static IHostEnvironment Instance => Mock.Of<IHostEnvironment>(h => h.ApplicationName == Name);
}
