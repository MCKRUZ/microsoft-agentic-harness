using Application.AI.Common.Interfaces.Telemetry;
using Infrastructure.AI.Telemetry.Redaction;

namespace Application.AI.Common.Tests;

/// <summary>
/// The one <see cref="IContentRedactionFilter"/> instance every test in this assembly that needs to
/// satisfy a redaction-filter constructor dependency should use.
/// </summary>
/// <remarks>
/// <see cref="DefaultContentRedactionFilter"/> is documented stateless and thread-safe, so a single
/// shared instance is safe everywhere — before this existed, ~20 test files each declared their own
/// local field, DI registration, or inline <c>new DefaultContentRedactionFilter()</c>, in at least
/// four different idioms for the identical need.
/// </remarks>
internal static class TestRedactionFilter
{
    public static readonly IContentRedactionFilter Instance = new DefaultContentRedactionFilter();
}
