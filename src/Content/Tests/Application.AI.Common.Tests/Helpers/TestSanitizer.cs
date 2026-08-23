using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Moq;

namespace Application.AI.Common.Tests;

/// <summary>
/// The one <see cref="ICompositeResponseSanitizer"/> instance every test in this assembly that needs
/// to satisfy a sanitizer constructor dependency without asserting on sanitization itself should use.
/// </summary>
/// <remarks>
/// A passthrough double — <see cref="ICompositeResponseSanitizer.Sanitize"/> returns the input
/// unchanged, wrapped as <see cref="SanitizationResult.Clean(string)"/> — mirroring
/// <see cref="TestRedactionFilter"/>'s reasoning: a constructor dependency a test doesn't mean to
/// exercise shouldn't need its own local mock declared per test class.
/// </remarks>
internal static class TestSanitizer
{
    public static readonly ICompositeResponseSanitizer Instance = BuildPassthrough();

    // Not Mock.Of's LINQ shorthand: that syntax evaluates its return expression once at setup time,
    // not per call, so It.IsAny<string>() in the return position would echo a fixed (null) value
    // rather than the actual content passed on each call — a passthrough double must echo back
    // whatever it was actually given.
    private static ICompositeResponseSanitizer BuildPassthrough()
    {
        var mock = new Mock<ICompositeResponseSanitizer>();
        mock.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));
        return mock.Object;
    }
}
