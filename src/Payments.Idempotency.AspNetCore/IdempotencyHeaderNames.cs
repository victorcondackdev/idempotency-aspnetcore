namespace Payments.Idempotency.AspNetCore;

/// <summary>HTTP header names used by the idempotency middleware.</summary>
public static class IdempotencyHeaderNames
{
    /// <summary>The default request header carrying the client-generated key: <c>Idempotency-Key</c>.</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The response header set to <c>true</c> on replayed responses: <c>Idempotent-Replayed</c>.</summary>
    public const string Replayed = "Idempotent-Replayed";
}
