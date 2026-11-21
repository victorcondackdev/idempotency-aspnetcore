using Microsoft.AspNetCore.Mvc;
using Payments.Idempotency.AspNetCore;

namespace Sample.Api.Controllers;

/// <summary>Request body for POST /refunds.</summary>
public sealed record RefundRequest(string PaymentId, long AmountCents);

/// <summary>Controllers opt in with <see cref="IdempotentAttribute"/>.</summary>
[ApiController]
[Route("refunds")]
public sealed class RefundsController : ControllerBase
{
    private static int s_executions;

    /// <summary>How many refunds actually executed.</summary>
    public static int Executions => Volatile.Read(ref s_executions);

    /// <summary>Creates a refund.</summary>
    [HttpPost]
    [Idempotent]
    public IActionResult Create(RefundRequest request)
    {
        Interlocked.Increment(ref s_executions);
        var id = "re_" + Guid.NewGuid().ToString("N");
        return Created($"/refunds/{id}", new { id, request.PaymentId, request.AmountCents, status = "pending" });
    }
}
