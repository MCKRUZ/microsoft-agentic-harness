using Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Common.Extensions;

/// <summary>
/// Shared HTTP mapping for failed <see cref="Result"/> instances so every controller in every
/// host returns identical status-code semantics for the same <see cref="ResultFailureType"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the single canonical copy of the failure→ProblemDetails switch. Controllers must not
/// hand-roll their own variant: the per-controller copies this replaced had drifted (one lacked
/// the <see cref="ResultFailureType.NotFound"/> arm, another lacked
/// <see cref="ResultFailureType.Conflict"/>), which silently turned well-defined failures into
/// opaque 500s on some routes but not others.
/// </para>
/// <para>
/// The canonical mapping: <see cref="ResultFailureType.Validation"/> → 400,
/// <see cref="ResultFailureType.Unauthorized"/> → 401,
/// <see cref="ResultFailureType.Forbidden"/> → 403,
/// <see cref="ResultFailureType.NotFound"/> → 404,
/// <see cref="ResultFailureType.Conflict"/> → 409, and every other failure type → 500 with the
/// caller-supplied title and a deliberately generic detail line — internal error text must never
/// leave the trust boundary on an unclassified failure.
/// </para>
/// </remarks>
public static class ControllerBaseExtensions
{
    /// <summary>
    /// Converts a failed <see cref="Result"/> into the canonical RFC 7807 ProblemDetails
    /// response for its <see cref="Result.FailureType"/>.
    /// </summary>
    /// <param name="controller">The controller producing the response; its
    /// <see cref="ControllerBase.Problem(string?, string?, int?, string?, string?)"/> factory is
    /// used so host-level ProblemDetails customization (trace ids, extensions) still applies.</param>
    /// <param name="result">The failed result to translate. Callers should check
    /// <see cref="Result.IsSuccess"/> first; passing a successful result yields the 500 arm.</param>
    /// <param name="serverErrorTitle">Problem title used for the unclassified (500) arm — one
    /// short operation-scoped phrase such as <c>"Memory operation failed"</c>.</param>
    /// <returns>The mapped <see cref="IActionResult"/>.</returns>
    public static IActionResult FailureResponse(
        this ControllerBase controller,
        Result result,
        string serverErrorTitle)
    {
        var (statusCode, title) = result.FailureType switch
        {
            ResultFailureType.Validation => (StatusCodes.Status400BadRequest, "Validation failed"),
            ResultFailureType.Unauthorized => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            ResultFailureType.Forbidden => (StatusCodes.Status403Forbidden, "Forbidden"),
            ResultFailureType.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            ResultFailureType.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
            _ => (StatusCodes.Status500InternalServerError, serverErrorTitle),
        };

        // Unclassified failures may carry raw exception text (store internals, paths); the
        // classified arms carry validator/lookup strings that are safe to surface verbatim.
        var detail = statusCode == StatusCodes.Status500InternalServerError
            ? "An error occurred processing the request. See server logs for details."
            : string.Join(" / ", result.Errors);

        return controller.Problem(title: title, detail: detail, statusCode: statusCode);
    }
}
