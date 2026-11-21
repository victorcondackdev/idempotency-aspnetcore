using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Payments.Idempotency.AspNetCore.Internal;

/// <summary>RFC 9457 problem details written by the middleware. Each carries a stable <c>code</c> extension.</summary>
internal static class IdempotencyProblems
{
    public static Task MissingKeyAsync(HttpContext context, string headerName) =>
        WriteAsync(
            context,
            StatusCodes.Status400BadRequest,
            "Idempotency key required",
            $"This endpoint requires an '{headerName}' header with a unique value per logical operation.",
            "idempotency_key_missing");

    public static Task InvalidKeyAsync(HttpContext context, string headerName, int maxLength) =>
        WriteAsync(
            context,
            StatusCodes.Status400BadRequest,
            "Invalid idempotency key",
            $"The '{headerName}' header must be a single value of 1 to {maxLength} visible ASCII characters.",
            "idempotency_key_invalid");

    public static Task FingerprintMismatchAsync(HttpContext context) =>
        WriteAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "Idempotency key reused with a different request",
            "This idempotency key was already used with a different request. Generate a new key for a new operation.",
            "idempotency_key_mismatch");

    public static Task InFlightAsync(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return WriteAsync(
            context,
            StatusCodes.Status409Conflict,
            "Request in progress",
            "A request with this idempotency key is still being processed. Retry after the indicated delay.",
            "idempotency_request_in_progress");
    }

    public static Task NotReplayableAsync(HttpContext context) =>
        WriteAsync(
            context,
            StatusCodes.Status409Conflict,
            "Response not replayable",
            "A request with this idempotency key was already processed, but its response could not be stored for replay.",
            "idempotency_response_not_replayable");

    private static Task WriteAsync(HttpContext context, int status, string title, string detail, string code)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code };
        var result = Results.Problem(detail: detail, statusCode: status, title: title, extensions: extensions);
        return result.ExecuteAsync(context);
    }
}
