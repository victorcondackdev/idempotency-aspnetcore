using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Payments.Idempotency.AspNetCore.Tests;

public sealed class IdempotencyMiddlewareTests
{
    [Fact]
    public async Task Replays_the_stored_response_for_the_same_key_and_request()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () =>
            {
                var n = Interlocked.Increment(ref executions);
                return Results.Created($"/payments/{n}", new { id = n });
            })
            .RequireIdempotency());

        using var first = await app.SendAsync(TestApp.Post("/payments", "key-1"));
        using var second = await app.SendAsync(TestApp.Post("/payments", "key-1"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.IsReplayed());

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.IsReplayed());
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal("application/json", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Same_key_with_a_different_request_returns_422()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref executions)))
            .RequireIdempotency());

        using var first = await app.SendAsync(TestApp.Post("/payments", "key-1", """{"amountCents":1000}"""));
        using var second = await app.SendAsync(TestApp.Post("/payments", "key-1", """{"amountCents":9999}"""));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("idempotency_key_mismatch", await second.ProblemCodeAsync());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Concurrent_duplicates_get_409_with_Retry_After_while_the_first_is_in_flight()
    {
        const int Concurrency = 5;
        var executions = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", async () =>
            {
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                await gate.Task;
                return Results.Created("/payments/1", new { id = 1 });
            })
            .RequireIdempotency());

        var requests = Enumerable.Range(0, Concurrency)
            .Select(_ => app.SendAsync(TestApp.Post("/payments", "key-concurrent")))
            .ToArray();

        // One request holds the lock inside the handler; wait for every other one to be rejected.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (requests.Count(r => r.IsCompleted) < Concurrency - 1)
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        gate.SetResult();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            var conflicts = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToArray();
            Assert.Equal(Concurrency - 1, conflicts.Length);
            Assert.All(conflicts, r => Assert.Equal(TimeSpan.FromSeconds(1), r.Headers.RetryAfter?.Delta));
            Assert.Equal("idempotency_request_in_progress", await conflicts[0].ProblemCodeAsync());
            Assert.Equal(1, executions);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        // Once completed, the same key replays.
        using var replay = await app.SendAsync(TestApp.Post("/payments", "key-concurrent"));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.IsReplayed());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Entries_expire_after_the_time_to_live()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref executions)))
            .RequireIdempotency());

        using var first = await app.SendAsync(TestApp.Post("/payments", "key-ttl"));

        app.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        using var beforeExpiry = await app.SendAsync(TestApp.Post("/payments", "key-ttl"));
        Assert.True(beforeExpiry.IsReplayed());
        Assert.Equal(1, executions);

        app.Time.Advance(TimeSpan.FromSeconds(1));
        using var afterExpiry = await app.SendAsync(TestApp.Post("/payments", "key-ttl"));
        Assert.Equal(HttpStatusCode.OK, afterExpiry.StatusCode);
        Assert.False(afterExpiry.IsReplayed());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Endpoint_time_to_live_overrides_the_default()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/captures", () => Results.Ok(Interlocked.Increment(ref executions)))
            .RequireIdempotency(timeToLive: TimeSpan.FromMinutes(5)));

        using var first = await app.SendAsync(TestApp.Post("/captures", "key-short"));
        app.Time.Advance(TimeSpan.FromMinutes(5));
        using var second = await app.SendAsync(TestApp.Post("/captures", "key-short"));

        Assert.False(second.IsReplayed());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Server_errors_are_not_stored_so_the_client_can_retry()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Interlocked.Increment(ref executions) == 1
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Created("/payments/1", new { id = 1 }))
            .RequireIdempotency());

        using var failed = await app.SendAsync(TestApp.Post("/payments", "key-5xx"));
        using var retried = await app.SendAsync(TestApp.Post("/payments", "key-5xx"));
        using var replayed = await app.SendAsync(TestApp.Post("/payments", "key-5xx"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.IsReplayed());
        Assert.True(replayed.IsReplayed());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Server_errors_can_be_cached_when_configured()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(
            a => a
                .MapPost("/payments", () =>
                {
                    Interlocked.Increment(ref executions);
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                })
                .RequireIdempotency(),
            o => o.CacheServerErrors = true);

        using var first = await app.SendAsync(TestApp.Post("/payments", "key-500"));
        using var second = await app.SendAsync(TestApp.Post("/payments", "key-500"));

        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
        Assert.True(second.IsReplayed());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Exceptions_release_the_key()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(
            a => a
                .MapPost("/payments", () => Interlocked.Increment(ref executions) == 1
                    ? throw new InvalidOperationException("processor timeout")
                    : Results.Ok("done"))
                .RequireIdempotency(),
            beforeIdempotency: a => a.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (InvalidOperationException)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }));

        using var failed = await app.SendAsync(TestApp.Post("/payments", "key-throw"));
        using var retried = await app.SendAsync(TestApp.Post("/payments", "key-throw"));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Missing_key_returns_400_when_required()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref executions)))
            .RequireIdempotency());

        using var response = await app.SendAsync(TestApp.Post("/payments", key: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_missing", await response.ProblemCodeAsync());
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task Missing_key_executes_normally_when_optional()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref executions)))
            .RequireIdempotency(required: false));

        using var first = await app.SendAsync(TestApp.Post("/payments", key: null));
        using var second = await app.SendAsync(TestApp.Post("/payments", key: null));
        using var keyed = await app.SendAsync(TestApp.Post("/payments", "key-optional"));
        using var replay = await app.SendAsync(TestApp.Post("/payments", "key-optional"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(replay.IsReplayed());
        Assert.Equal(3, executions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("naïve")]
    public async Task Invalid_keys_return_400(string key)
    {
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Results.Ok())
            .RequireIdempotency());

        using var response = await app.SendAsync(TestApp.Post("/payments", key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_keys_return_400()
    {
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/payments", () => Results.Ok())
            .RequireIdempotency());

        using var response = await app.SendAsync(TestApp.Post("/payments", new string('k', 256)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_invalid", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Keys_are_scoped_by_the_scope_resolver()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(
            a => a
                .MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref executions)))
                .RequireIdempotency(),
            o => o.ScopeResolver = context => context.Request.Headers["X-Tenant"].ToString());

        using var tenantA = await app.SendAsync(TestApp.Post("/payments", "shared-key", tenant: "a"));
        using var tenantB = await app.SendAsync(TestApp.Post("/payments", "shared-key", tenant: "b"));
        using var tenantAAgain = await app.SendAsync(TestApp.Post("/payments", "shared-key", tenant: "a"));

        Assert.False(tenantB.IsReplayed());
        Assert.True(tenantAAgain.IsReplayed());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Keys_are_scoped_by_route()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a =>
        {
            a.MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref executions))).RequireIdempotency();
            a.MapPost("/refunds", () => Results.Ok(Interlocked.Increment(ref executions))).RequireIdempotency();
        });

        using var payment = await app.SendAsync(TestApp.Post("/payments", "key-route"));
        using var refund = await app.SendAsync(TestApp.Post("/refunds", "key-route"));

        Assert.False(refund.IsReplayed());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task Responses_over_the_size_limit_are_sent_but_not_replayed()
    {
        var executions = 0;
        var large = new string('x', 4096);
        await using var app = await TestApp.StartAsync(
            a => a
                .MapPost("/exports", () =>
                {
                    Interlocked.Increment(ref executions);
                    return Results.Text(large);
                })
                .RequireIdempotency(),
            o => o.MaxResponseBodySize = 1024);

        using var first = await app.SendAsync(TestApp.Post("/exports", "key-large"));
        using var second = await app.SendAsync(TestApp.Post("/exports", "key-large"));

        Assert.Equal(large, await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("idempotency_response_not_replayable", await second.ProblemCodeAsync());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Endpoints_without_metadata_are_untouched()
    {
        var executions = 0;
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/ping", () => Results.Ok(Interlocked.Increment(ref executions))));

        using var first = await app.SendAsync(TestApp.Post("/ping", "key-ignored"));
        using var second = await app.SendAsync(TestApp.Post("/ping", "key-ignored"));

        Assert.False(second.IsReplayed());
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task The_handler_can_still_read_the_request_body()
    {
        await using var app = await TestApp.StartAsync(a => a
            .MapPost("/echo", (Charge charge) => Results.Ok(charge))
            .RequireIdempotency());

        using var response = await app.SendAsync(TestApp.Post("/echo", "key-body", """{"amountCents":4321}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("4321", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public sealed record Charge(long AmountCents);
}
