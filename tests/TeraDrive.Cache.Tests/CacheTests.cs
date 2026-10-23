using TeraDrive.Cache;
using TeraDrive.Core;
using Xunit;

namespace TeraDrive.Cache.Tests;

public sealed class Tmp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "td-cache-" + Guid.NewGuid().ToString("N"));
    public Tmp() { Directory.CreateDirectory(Path); }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class CacheTests
{
    static Func<string, CancellationToken, Task> Data(int size, byte fill = 1) =>
        async (p, ct) => await File.WriteAllBytesAsync(p, Enumerable.Repeat(fill, size).ToArray(), ct);

    [Fact] public async Task FetchStoresAndIndexes()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000);
        using (var l = await c.GetOrFetchAsync("k", "p", "v1", 100, Data(100), default)) Assert.True(File.Exists(l.Path));
        Assert.Equal(100, c.UsedBytes); Assert.Equal(1, c.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Path, "tmp"))); // temp cleaned
    }

    [Fact] public async Task HitDoesNotRefetch_StaleVersionRefetches()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000); var calls = 0;
        Func<string, CancellationToken, Task> f = (p, ct) => { calls++; return File.WriteAllBytesAsync(p, new byte[10], ct); };
        (await c.GetOrFetchAsync("k", "p", "v1", 10, f, default)).Dispose();
        (await c.GetOrFetchAsync("k", "p", "v1", 10, f, default)).Dispose();
        Assert.Equal(1, calls);
        (await c.GetOrFetchAsync("k", "p", "v2", 10, f, default)).Dispose();
        Assert.Equal(2, calls);
    }

    [Fact] public async Task ConcurrentRequestsShareOneFetch()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000); var calls = 0;
        Func<string, CancellationToken, Task> f = async (p, ct) => { Interlocked.Increment(ref calls); await Task.Delay(100, ct); await File.WriteAllBytesAsync(p, new byte[10], ct); };
        var leases = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.GetOrFetchAsync("k", "p", "v", 10, f, default)));
        foreach (var l in leases) l.Dispose();
        Assert.Equal(1, calls);
    }

    [Fact] public async Task LruEvictsOldestUnpinned()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 250);
        (await c.GetOrFetchAsync("a", "p", null, 100, Data(100, 1), default)).Dispose(); await Task.Delay(20);
        (await c.GetOrFetchAsync("b", "p", null, 100, Data(100, 2), default)).Dispose(); await Task.Delay(20);
        (await c.GetOrFetchAsync("a", "p", null, 100, Data(100, 1), default)).Dispose(); // touch a => b is oldest
        await Task.Delay(20);
        (await c.GetOrFetchAsync("c", "p", null, 100, Data(100, 3), default)).Dispose();
        Assert.NotNull(c.Peek("a")); Assert.Null(c.Peek("b")); Assert.NotNull(c.Peek("c"));
        Assert.True(c.UsedBytes <= 250);
    }

    [Fact] public async Task PinnedAndInUseAreNeverEvicted()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 200);
        (await c.GetOrFetchAsync("pin", "p", null, 100, Data(100), default)).Dispose(); c.Pin("pin");
        var lease = await c.GetOrFetchAsync("busy", "p", null, 100, Data(100), default);
        await Assert.ThrowsAsync<CacheLimitException>(() => c.GetOrFetchAsync("new", "p", null, 100, Data(100), default));
        Assert.NotNull(c.Peek("pin")); Assert.True(File.Exists(lease.Path));
        Assert.False(c.Remove("busy"));
        lease.Dispose();
        (await c.GetOrFetchAsync("new", "p", null, 100, Data(100), default)).Dispose(); // busy now evictable
        Assert.Null(c.Peek("busy")); Assert.NotNull(c.Peek("pin"));
    }

    [Fact] public async Task ClearUnpinnedKeepsPinnedAndNeverTouchesOutsideFiles()
    {
        using var t = new Tmp(); var outside = Path.Combine(t.Path, "user.txt"); File.WriteAllText(outside, "mine");
        var root = Path.Combine(t.Path, "cache"); using var c = new CacheManager(root, 1000);
        (await c.GetOrFetchAsync("a", "p", null, 10, Data(10), default)).Dispose();
        (await c.GetOrFetchAsync("b", "p", null, 10, Data(10), default)).Dispose(); c.Pin("b");
        Assert.Equal(1, c.ClearUnpinned());
        Assert.Null(c.Peek("a")); Assert.NotNull(c.Peek("b")); Assert.True(File.Exists(outside));
    }

    [Fact] public async Task IncompleteDownloadIsRejectedAndNotCached()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000);
        await Assert.ThrowsAsync<IOException>(() => c.GetOrFetchAsync("k", "p", null, 100, Data(50), default));
        Assert.Equal(0, c.Count); Assert.Empty(Directory.GetFiles(Path.Combine(t.Path, "tmp")));
    }

    [Fact] public async Task FileLargerThanLimitRejected()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 50);
        await Assert.ThrowsAsync<CacheLimitException>(() => c.GetOrFetchAsync("k", "p", null, 100, Data(100), default));
    }

    [Fact] public async Task IndexPersistsAcrossRestartAndRecoversFromJunk()
    {
        using var t = new Tmp();
        using (var c = new CacheManager(t.Path, 1000)) { (await c.GetOrFetchAsync("a", "p", "v", 10, Data(10), default)).Dispose(); c.Pin("a"); }
        File.WriteAllText(Path.Combine(t.Path, "tmp", "stale.part"), "x");
        File.WriteAllText(Path.Combine(t.Path, "objects", "orphan.bin"), "x");
        using var c2 = new CacheManager(t.Path, 1000);
        Assert.True(c2.Peek("a")!.Pinned); Assert.Equal(1, c2.Count);
        Assert.False(File.Exists(Path.Combine(t.Path, "tmp", "stale.part"))); Assert.False(File.Exists(Path.Combine(t.Path, "objects", "orphan.bin")));
    }

    [Fact] public async Task IntegrityCheckRemovesCorruptEntries()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000);
        string path; using (var l = await c.GetOrFetchAsync("a", "p", null, 10, Data(10), default)) path = l.Path;
        (await c.GetOrFetchAsync("b", "p", null, 10, Data(10, 9), default)).Dispose();
        File.WriteAllBytes(path, new byte[10]); // same size, different content
        var bad = await c.VerifyIntegrityAsync(default);
        Assert.Equal(new[] { "a" }, bad); Assert.NotNull(c.Peek("b"));
    }

    [Fact] public async Task KeyCollisionsOfSimilarNamesStaySeparate()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000);
        (await c.GetOrFetchAsync("p:/a/b.txt", "p", null, 10, Data(10, 1), default)).Dispose();
        (await c.GetOrFetchAsync("p:/a_b.txt", "p", null, 10, Data(10, 2), default)).Dispose();
        Assert.Equal(2, c.Count);
    }

    [Fact] public async Task LoweringLimitEvicts()
    {
        using var t = new Tmp(); using var c = new CacheManager(t.Path, 1000);
        for (var i = 0; i < 5; i++) (await c.GetOrFetchAsync("k" + i, "p", null, 100, Data(100), default)).Dispose();
        c.SetLimit(250); Assert.True(c.UsedBytes <= 250);
    }
}
