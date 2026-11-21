using System.Collections.Concurrent;
using Payments.Idempotency.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddSingleton<PaymentLedger>();
builder.Services.AddIdempotency(options =>
{
    // Keys are scoped per tenant: two tenants may use the same key independently.
    options.ScopeResolver = context => context.Request.Headers["X-Tenant-Id"].ToString();
});

var app = builder.Build();

app.UseIdempotency();

app.MapPost("/payments", (CreatePayment request, PaymentLedger ledger) =>
    {
        var payment = ledger.Create(request);
        return Results.Created($"/payments/{payment.Id}", payment);
    })
    .RequireIdempotency();

app.MapPost("/payments/{id}/capture", (string id, PaymentLedger ledger) =>
        ledger.Capture(id) is { } payment ? Results.Ok(payment) : Results.NotFound())
    .RequireIdempotency(timeToLive: TimeSpan.FromHours(1));

app.MapGet("/payments/{id}", (string id, PaymentLedger ledger) =>
    ledger.Find(id) is { } payment ? Results.Ok(payment) : Results.NotFound());

app.MapControllers();

app.Run();

/// <summary>Request body for POST /payments.</summary>
public sealed record CreatePayment(long AmountCents, string Currency, string Description);

/// <summary>A payment as returned by the API.</summary>
public sealed record Payment(string Id, long AmountCents, string Currency, string Status);

/// <summary>In-memory stand-in for a payment processor; counts executions to make replays visible.</summary>
public sealed class PaymentLedger
{
    private readonly ConcurrentDictionary<string, Payment> _payments = new(StringComparer.Ordinal);
    private int _executions;

    /// <summary>How many times a side-effecting operation actually ran.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>Creates a payment.</summary>
    public Payment Create(CreatePayment request)
    {
        Interlocked.Increment(ref _executions);
        var payment = new Payment("pay_" + Guid.NewGuid().ToString("N"), request.AmountCents, request.Currency, "authorized");
        _payments[payment.Id] = payment;
        return payment;
    }

    /// <summary>Captures a payment.</summary>
    public Payment? Capture(string id)
    {
        Interlocked.Increment(ref _executions);
        if (!_payments.TryGetValue(id, out var payment))
        {
            return null;
        }

        var captured = payment with { Status = "captured" };
        _payments[id] = captured;
        return captured;
    }

    /// <summary>Finds a payment.</summary>
    public Payment? Find(string id) => _payments.TryGetValue(id, out var payment) ? payment : null;
}

/// <summary>Entry point, public so the integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program
{
}
