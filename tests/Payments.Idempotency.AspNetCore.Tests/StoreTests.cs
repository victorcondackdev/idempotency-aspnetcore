using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Payments.Idempotency.AspNetCore.Tests;

public sealed class StoreTests
{
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public static TheoryData<string> Stores => new() { "memory", "distributed" };

    private static (IIdempotencyStore Store, FakeTimeProvider Time) Create(string kind)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        IIdempotencyStore store = kind == "memory"
            ? new InMemoryIdempotencyStore(time)
            : new DistributedCacheIdempotencyStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), time);
        return (store, time);
    }

    private static StoredResponse Response(int status = 201) => new()
    {
        StatusCode = status,
        Headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
        Body = "{\"id\":1}"u8.ToArray(),
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Begin_complete_and_get(string kind)
    {
        var (store, _) = Create(kind);

        var begin = await store.TryBeginAsync("k", "fp", Lock);
        Assert.True(begin.Acquired);
        Assert.NotNull(begin.LockToken);

        var second = await store.TryBeginAsync("k", "fp", Lock);
        Assert.False(second.Acquired);
        Assert.Equal(IdempotencyEntryState.InFlight, second.Existing?.State);

        Assert.True(await store.CompleteAsync("k", begin.LockToken, Response(), Ttl));

        var entry = await store.GetAsync("k");
        Assert.NotNull(entry);
        Assert.Equal(IdempotencyEntryState.Completed, entry.State);
        Assert.Equal("fp", entry.Fingerprint);
        Assert.NotNull(entry.Response);
        Assert.Equal(201, entry.Response.StatusCode);
        Assert.Equal("{\"id\":1}"u8.ToArray(), entry.Response.Body);
        Assert.Equal(new[] { "application/json" }, entry.Response.Headers["Content-Type"]);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Only_the_lock_holder_can_complete_or_release(string kind)
    {
        var (store, _) = Create(kind);
        var begin = await store.TryBeginAsync("k", "fp", Lock);

        Assert.False(await store.CompleteAsync("k", "not-the-token", Response(), Ttl));
        Assert.False(await store.ReleaseAsync("k", "not-the-token"));
        Assert.True(await store.ReleaseAsync("k", begin.LockToken!));
        Assert.Null(await store.GetAsync("k"));
        Assert.True((await store.TryBeginAsync("k", "fp", Lock)).Acquired);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Completed_entries_cannot_be_released(string kind)
    {
        var (store, _) = Create(kind);
        var begin = await store.TryBeginAsync("k", "fp", Lock);
        Assert.True(await store.CompleteAsync("k", begin.LockToken!, null, Ttl));

        Assert.False(await store.ReleaseAsync("k", begin.LockToken!));
        var entry = await store.GetAsync("k");
        Assert.NotNull(entry);
        Assert.Null(entry.Response);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Abandoned_locks_expire_and_can_be_taken_over(string kind)
    {
        var (store, time) = Create(kind);
        var abandoned = await store.TryBeginAsync("k", "fp", Lock);

        time.Advance(Lock);
        var takeover = await store.TryBeginAsync("k", "fp", Lock);

        Assert.True(takeover.Acquired);
        Assert.NotEqual(abandoned.LockToken, takeover.LockToken);
        Assert.False(await store.CompleteAsync("k", abandoned.LockToken!, Response(), Ttl));
        Assert.True(await store.CompleteAsync("k", takeover.LockToken!, Response(), Ttl));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Completed_entries_expire_after_the_time_to_live(string kind)
    {
        var (store, time) = Create(kind);
        var begin = await store.TryBeginAsync("k", "fp", Lock);
        await store.CompleteAsync("k", begin.LockToken!, Response(), Ttl);

        time.Advance(Ttl - TimeSpan.FromTicks(1));
        Assert.NotNull(await store.GetAsync("k"));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await store.GetAsync("k"));
    }

    [Fact]
    public async Task In_memory_store_grants_exactly_one_lock_under_contention()
    {
        var store = new InMemoryIdempotencyStore(new FakeTimeProvider());

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => store.TryBeginAsync("contended", "fp", Lock))));

        Assert.Single(results, r => r.Acquired);
        Assert.NotNull(await store.GetAsync("contended"));
    }

    [Fact]
    public void Begin_results_validate_their_arguments()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyBeginResult.Locked(string.Empty));
        Assert.Throws<ArgumentNullException>(() => IdempotencyBeginResult.Exists(null!));
    }
}
