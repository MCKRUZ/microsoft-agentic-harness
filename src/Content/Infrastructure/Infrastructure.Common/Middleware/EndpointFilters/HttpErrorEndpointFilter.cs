using Microsoft.AspNetCore.Http;

namespace Infrastructure.Common.Middleware.EndpointFilters;

/// <summary>
/// Endpoint filter that catches <see cref="BadHttpRequestException"/> for
/// payload-too-large (413) errors and converts them to structured problem responses.
/// </summary>
/// <remarks>
/// <para>
/// Apply to minimal API endpoints or endpoint groups that accept request bodies:
/// <code>
/// app.MapPost("/upload", handler).AddEndpointFilter&lt;HttpErrorEndpointFilter&gt;();
/// </code>
/// </para>
/// <para>
/// Without this filter, a 413 from Kestrel produces an empty response.
/// This filter ensures clients receive a machine-readable RFC 7807 Problem Details payload.
/// </para>
/// </remarks>
public sealed class HttpErrorEndpointFilter : IEndpointFilter
{
    /// <summary>
    /// Invokes the next filter in the pipeline and catches payload-too-large errors.
    /// </summary>
    /// <param name="context">The endpoint filter invocation context.</param>
    /// <param name="next">The next filter delegate in the pipeline.</param>
    /// <returns>
    /// The result from the next filter, or a Problem Details result when a 413 error occurs.
    /// </returns>
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }
}
