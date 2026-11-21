using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Payments.Idempotency.AspNetCore.Tests;

/// <summary>A minimal API hosted on TestServer with the idempotency middleware and a fake clock.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string HeaderName = "Idempotency-Key";

    private TestApp(WebApplication app, HttpClient client, FakeTimeProvider time)
    {
        App = app;
        Client = client;
        Time = time;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public FakeTimeProvider Time { get; }

    public static async Task<TestApp> StartAsync(
        Action<WebApplication> mapEndpoints,
        Action<IdempotencyOptions>? configure = null,
        Action<WebApplication>? beforeIdempotency = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddIdempotency(configure);

        var app = builder.Build();
        beforeIdempotency?.Invoke(app);
        app.UseIdempotency();
        mapEndpoints(app);

        await app.StartAsync();
        return new TestApp(app, app.GetTestClient(), time);
    }

    public static HttpRequestMessage Post(string path, string? key, string json = """{"amountCents":1000}""", string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(HeaderName, key);
        }

        if (tenant is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Tenant", tenant);
        }

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            return await Client.SendAsync(request);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

internal static class ResponseAssertions
{
    public static bool IsReplayed(this HttpResponseMessage response) =>
        response.Headers.TryGetValues(IdempotencyHeaderNames.Replayed, out var values) && values.Contains("true");

    public static async Task<string> ProblemCodeAsync(this HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }
}
