using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Payments.Idempotency.AspNetCore.Internal;

/// <summary>
/// Implements the idempotency state machine for endpoints carrying <see cref="IIdempotencyMetadata"/>:
/// absent → in flight → completed (replayed) | released (5xx, 408, 429, exception) | expired.
/// </summary>
internal sealed class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly IOptions<IdempotencyOptions> _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<IIdempotencyMetadata>();
        if (metadata is null)
        {
            await _next(context);
            return;
        }

        var options = _options.Value;
        var values = context.Request.Headers[options.HeaderName];
        if (values.Count == 0)
        {
            if (metadata.Required)
            {
                await IdempotencyProblems.MissingKeyAsync(context, options.HeaderName);
                return;
            }

            await _next(context);
            return;
        }

        var key = values.Count == 1 ? values[0] : null;
        if (!IsValidKey(key, options.MaxKeyLength))
        {
            await IdempotencyProblems.InvalidKeyAsync(context, options.HeaderName, options.MaxKeyLength);
            return;
        }

        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? string.Empty;
        var scope = options.ScopeResolver(context) ?? string.Empty;
        var storeKey = StoreKey.Create(options.StoreKeyPrefix, scope, context.Request.Method, route, key);
        var fingerprint = await RequestFingerprint.ComputeAsync(context.Request, options, context.RequestAborted);

        var begin = await _store.TryBeginAsync(storeKey, fingerprint, options.LockTimeout, context.RequestAborted);
        if (begin.Existing is { } existing)
        {
            await HandleExistingAsync(context, existing, fingerprint, options);
            return;
        }

        if (begin.LockToken is not { } lockToken)
        {
            throw new InvalidOperationException($"{_store.GetType().Name}.TryBeginAsync returned neither a lock nor an existing entry.");
        }

        await ExecuteAsync(context, storeKey, lockToken, metadata, options);
    }

    private async Task ExecuteAsync(HttpContext context, string storeKey, string lockToken, IIdempotencyMetadata metadata, IdempotencyOptions options)
    {
        var originalBody = context.Response.Body;
        using var capture = new ResponseCaptureStream(originalBody, options.MaxResponseBodySize);
        context.Response.Body = capture;
        try
        {
            await _next(context);
        }
        catch
        {
            // The outcome is unknown to the client: free the key so a retry can run.
            context.Response.Body = originalBody;
            await ReleaseAsync(storeKey, lockToken);
            throw;
        }

        try
        {
            // Push anything still buffered in the response PipeWriter through the capture stream
            // before the original body is restored. The handler already succeeded, so a failure
            // here (client gone) must not release the key.
            await context.Response.BodyWriter.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Client disconnected before the idempotent response was flushed");
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var status = context.Response.StatusCode;
        if ((status >= StatusCodes.Status500InternalServerError && !options.CacheServerErrors) || options.ReleasedStatusCodes.Contains(status))
        {
            await ReleaseAsync(storeKey, lockToken);
            return;
        }

        StoredResponse? stored = null;
        if (capture.Overflowed)
        {
            _logger.LogWarning(
                "Response for idempotent request {Method} {Path} exceeded {Limit} bytes and will not be replayed",
                context.Request.Method,
                context.Request.Path.Value,
                options.MaxResponseBodySize);
        }
        else
        {
            stored = new StoredResponse
            {
                StatusCode = status,
                Headers = CaptureHeaders(context.Response, options),
                Body = capture.ToArray(),
            };
        }

        // Never cancel this write: the side effects already happened and must be recorded.
        var ttl = metadata.TimeToLive ?? options.TimeToLive;
        if (!await _store.CompleteAsync(storeKey, lockToken, stored, ttl, CancellationToken.None))
        {
            _logger.LogWarning(
                "Idempotency lock for {Method} {Path} expired before the request completed; consider a longer LockTimeout",
                context.Request.Method,
                context.Request.Path.Value);
        }
    }

    private async Task HandleExistingAsync(HttpContext context, IdempotencyEntry existing, string fingerprint, IdempotencyOptions options)
    {
        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            await IdempotencyProblems.FingerprintMismatchAsync(context);
            return;
        }

        if (existing.State == IdempotencyEntryState.InFlight)
        {
            await IdempotencyProblems.InFlightAsync(context, options.RetryAfter);
            return;
        }

        if (existing.Response is not { } response)
        {
            await IdempotencyProblems.NotReplayableAsync(context);
            return;
        }

        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = new StringValues(header.Value);
        }

        context.Response.Headers[IdempotencyHeaderNames.Replayed] = "true";
        if (response.Body.Length > 0)
        {
            context.Response.ContentLength = response.Body.Length;
            await context.Response.Body.WriteAsync(response.Body.AsMemory(), context.RequestAborted);
        }
    }

    private async Task ReleaseAsync(string storeKey, string lockToken)
    {
        try
        {
            await _store.ReleaseAsync(storeKey, lockToken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The lock will expire after LockTimeout; do not mask the original outcome.
            _logger.LogError(ex, "Failed to release idempotency lock {StoreKey}", storeKey);
        }
    }

    private static Dictionary<string, string[]> CaptureHeaders(HttpResponse response, IdempotencyOptions options)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (response.ContentType is { Length: > 0 } contentType)
        {
            headers["Content-Type"] = [contentType];
        }

        foreach (var name in options.ReplayedResponseHeaders)
        {
            if (response.Headers.TryGetValue(name, out var values) && values.Count > 0)
            {
                var copy = new string[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    copy[i] = values[i] ?? string.Empty;
                }

                headers[name] = copy;
            }
        }

        return headers;
    }

    private static bool IsValidKey([NotNullWhen(true)] string? key, int maxLength)
    {
        if (string.IsNullOrEmpty(key) || key.Length > maxLength)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
