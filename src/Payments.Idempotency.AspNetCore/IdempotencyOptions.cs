using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Payments.Idempotency.AspNetCore;

/// <summary>Options for the idempotency middleware, configured with <c>services.AddIdempotency(o =&gt; ...)</c>.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>The request header carrying the idempotency key. Defaults to <c>Idempotency-Key</c>.</summary>
    public string HeaderName { get; set; } = IdempotencyHeaderNames.IdempotencyKey;

    /// <summary>
    /// The maximum key length. Longer keys are rejected with <c>400</c>. Defaults to 255, which
    /// fits a UUID, a ULID or any reasonable client-generated token.
    /// </summary>
    public int MaxKeyLength { get; set; } = 255;

    /// <summary>
    /// How long a completed response is kept and replayed. Defaults to 24 hours, like Stripe.
    /// Endpoints can override it with <c>RequireIdempotency(timeToLive: ...)</c> or
    /// <see cref="IdempotentAttribute.TimeToLiveSeconds"/>.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an in-flight lock lives before it is considered abandoned (for example after a
    /// crash) and a retry may take it over. Must exceed the slowest expected handler, otherwise a
    /// retry could execute concurrently with the original request. Defaults to 1 minute.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The <c>Retry-After</c> sent with <c>409</c> while a request is in flight. Defaults to 1 second.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The largest response body, in bytes, that is captured for replay. Larger (or streamed)
    /// responses are still sent to the client, but the key is completed as not replayable: later
    /// retries get <c>409</c> instead of a second execution. Defaults to 1 MiB.
    /// </summary>
    public long MaxResponseBodySize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Whether <c>5xx</c> responses are stored and replayed. Defaults to <see langword="false"/>:
    /// the key is released so the client can retry a transient failure with the same key.
    /// </summary>
    public bool CacheServerErrors { get; set; }

    /// <summary>
    /// Status codes that release the key instead of being stored, so the client may retry with the
    /// same key. Defaults to <c>408 Request Timeout</c> and <c>429 Too Many Requests</c>.
    /// </summary>
    public ISet<int> ReleasedStatusCodes { get; } = new HashSet<int> { StatusCodes.Status408RequestTimeout, StatusCodes.Status429TooManyRequests };

    /// <summary>Whether the query string is part of the request fingerprint. Defaults to <see langword="false"/>.</summary>
    public bool IncludeQueryStringInFingerprint { get; set; }

    /// <summary>
    /// Request headers that are part of the fingerprint (case-insensitive names), for example
    /// <c>Content-Type</c> or a tenant header. Empty by default.
    /// </summary>
    public IList<string> FingerprintHeaders { get; } = new List<string>();

    /// <summary>
    /// Response headers stored and replayed in addition to <c>Content-Type</c>, which is always kept.
    /// Defaults to <c>Location</c>, <c>ETag</c>, <c>Cache-Control</c> and <c>Content-Language</c>.
    /// </summary>
    public IList<string> ReplayedResponseHeaders { get; } = new List<string> { "Location", "ETag", "Cache-Control", "Content-Language" };

    /// <summary>
    /// Resolves the scope a key belongs to, typically the tenant or the authenticated principal,
    /// so that two clients sending the same key never see each other's responses. Defaults to
    /// the <see cref="ClaimTypes.NameIdentifier"/> claim, then <c>Identity.Name</c>, then an empty
    /// scope shared by anonymous callers.
    /// </summary>
    public Func<HttpContext, string> ScopeResolver { get; set; } = DefaultScope;

    /// <summary>Prefix of the keys written to the store. Defaults to <c>idempotency</c>.</summary>
    public string StoreKeyPrefix { get; set; } = "idempotency";

    private static string DefaultScope(HttpContext context) =>
        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? context.User.Identity?.Name
        ?? string.Empty;
}
