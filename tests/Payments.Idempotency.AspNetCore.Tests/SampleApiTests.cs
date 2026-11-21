using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sample.Api.Controllers;

namespace Payments.Idempotency.AspNetCore.Tests;

/// <summary>End-to-end tests against samples/Sample.Api, hosted in memory.</summary>
public sealed class SampleApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SampleApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static HttpRequestMessage Post(string path, object body, string? key, string tenant = "acme")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Id", tenant);
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    [Fact]
    public async Task Minimal_api_payment_is_created_once_and_replayed()
    {
        using var client = _factory.CreateClient();
        var ledger = _factory.Services.GetRequiredService<PaymentLedger>();
        var key = Guid.NewGuid().ToString();
        var body = new { amountCents = 15_000, currency = "BRL", description = "Pedido 42" };
        var before = ledger.Executions;

        using var firstRequest = Post("/payments", body, key);
        using var first = await client.SendAsync(firstRequest);
        using var retryRequest = Post("/payments", body, key);
        using var retry = await client.SendAsync(retryRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);

        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(firstJson.RootElement.GetProperty("id").GetString(), retryJson.RootElement.GetProperty("id").GetString());
        Assert.Equal(before + 1, ledger.Executions);
    }

    [Fact]
    public async Task Tenants_do_not_share_keys()
    {
        using var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var body = new { amountCents = 100, currency = "BRL", description = "x" };

        using var acmeRequest = Post("/payments", body, key, tenant: "acme");
        using var acme = await client.SendAsync(acmeRequest);
        using var globexRequest = Post("/payments", body, key, tenant: "globex");
        using var globex = await client.SendAsync(globexRequest);

        Assert.Equal(HttpStatusCode.Created, globex.StatusCode);
        Assert.False(globex.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(acme.Headers.Location, globex.Headers.Location);
    }

    [Fact]
    public async Task Controller_action_with_Idempotent_attribute_is_replayed()
    {
        using var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var body = new { paymentId = "pay_1", amountCents = 500 };
        var before = RefundsController.Executions;

        using var firstRequest = Post("/refunds", body, key);
        using var first = await client.SendAsync(firstRequest);
        using var retryRequest = Post("/refunds", body, key);
        using var retry = await client.SendAsync(retryRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(before + 1, RefundsController.Executions);
    }

    [Fact]
    public async Task Controller_action_requires_the_key()
    {
        using var client = _factory.CreateClient();

        using var request = Post("/refunds", new { paymentId = "pay_1", amountCents = 500 }, key: null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
