using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MfaSrv.Server.Services;

namespace MfaSrv.Tests.Unit.Server;

public class RedisChallengeStoreTests
{
    private static RedisChallengeStore CreateStore()
    {
        var cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        return new RedisChallengeStore(cache);
    }

    [Fact]
    public async Task SetAndGet_RoundTripsValue()
    {
        var store = CreateStore();
        var value = new TestData { Name = "test", Count = 42 };

        await store.SetAsync("key1", value, TimeSpan.FromMinutes(5));
        var result = await store.GetAsync<TestData>("key1");

        result.Should().NotBeNull();
        result!.Name.Should().Be("test");
        result.Count.Should().Be(42);
    }

    [Fact]
    public async Task Get_NonexistentKey_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.GetAsync<TestData>("missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Remove_DeletesKey()
    {
        var store = CreateStore();
        await store.SetAsync("key1", new TestData { Name = "x" }, TimeSpan.FromMinutes(5));

        await store.RemoveAsync("key1");
        var result = await store.GetAsync<TestData>("key1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_OverwritesExistingValue()
    {
        var store = CreateStore();

        await store.SetAsync("key1", new TestData { Name = "first", Count = 1 }, TimeSpan.FromMinutes(5));
        await store.SetAsync("key1", new TestData { Name = "second", Count = 2 }, TimeSpan.FromMinutes(5));

        var result = await store.GetAsync<TestData>("key1");
        result.Should().NotBeNull();
        result!.Name.Should().Be("second");
        result.Count.Should().Be(2);
    }

    [Fact]
    public async Task Remove_NonexistentKey_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.RemoveAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetAndGet_ComplexType_RoundTrips()
    {
        var store = CreateStore();
        var value = new ComplexTestData
        {
            Id = "abc",
            Expiry = DateTimeOffset.UtcNow.AddMinutes(5),
            Data = new byte[] { 1, 2, 3 },
            Status = 2
        };

        await store.SetAsync("complex", value, TimeSpan.FromMinutes(5));
        var result = await store.GetAsync<ComplexTestData>("complex");

        result.Should().NotBeNull();
        result!.Id.Should().Be("abc");
        result.Data.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        result.Status.Should().Be(2);
    }

    private sealed class TestData
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class ComplexTestData
    {
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset Expiry { get; set; }
        public byte[]? Data { get; set; }
        public int Status { get; set; }
    }
}
