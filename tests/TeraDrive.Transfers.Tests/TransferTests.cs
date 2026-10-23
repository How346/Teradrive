using TeraDrive.Cache;
using TeraDrive.Cloud;
using TeraDrive.Core;
using TeraDrive.Transfers;
using Xunit;

namespace TeraDrive.Transfers.Tests;

sealed class Tmp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "td-xfer-" + Guid.NewGuid().ToString("N"));
    public Tmp() { Directory.CreateDirectory(Path); }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

/// <summary>Decorates the local provider with controllable failures / blocking for tests.</summary>
sealed class TestProvider : ICloudStorageProvider
{
    readonly LocalFileSystemProvider _inner;
    public int FailOpenTimes; public bool BlockFirstOpen; public int OpenCalls; public TaskCompletionSource Gate = new();
    public TestProvider(string root) { _inner = new LocalFileSystemProvider(root); }
    public string ProviderId => _inner.ProviderId; public string DisplayName => "test"; public ProviderCapabilities Capabilities => _inner.Capabilities; public bool IsConnected => _inner.IsConnected;
    public Task ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct) => _inner.DisconnectAsync(ct);
    public Task<AccountInfo> GetAccountInfoAsync(CancellationToken ct) => _inner.GetAccountInfoAsync(ct);
    public Task<IReadOnlyList<CloudItem>> ListDirectoryAsync(string p, CancellationToken ct) => _inner.ListDirectoryAsync(p, ct);
    public Task<CloudItem> GetMetadataAsync(string p, CancellationToken ct) => _inner.GetMetadataAsync(p, ct);
    public async Task<Stream> OpenReadAsync(string p, long o, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref OpenCalls);
        if (n <= FailOpenTimes) throw new IOException("transient network failure");
        if (BlockFirstOpen && n == 1) await Gate.Task.WaitAsync(ct);
        return await _inner.OpenReadAsync(p, o, ct);
    }
    public Task<CloudItem> UploadAsync(string p, Stream c, bool ow, Action<long>? pr, CancellationToken ct) => _inner.UploadAsync(p, c, ow, pr, ct);
    public Task<CloudItem> CreateDirectoryAsync(string p, CancellationToken ct) => _inner.CreateDirectoryAsync(p, ct);
    public Task<CloudItem> MoveAsync(string s, string d, bool ow, CancellationToken ct) => _inner.MoveAsync(s, d, ow, ct);
    public Task<CloudItem> RenameAsync(string p, string n, CancellationToken ct) => _inner.RenameAsync(p, n, ct);
    public Task DeleteAsync(string p, CancellationToken ct) => _inner.DeleteAsync(p, ct);
    public Task<QuotaInfo> GetQuotaAsync(CancellationToken ct) => _inner.GetQuotaAsync(ct);
}

public class StateMachineTests
{
    [Theory]
    [InlineData(TransferState.Queued, TransferState.Running, true)]
    [InlineData(TransferState.Running, TransferState.Completed, true)]
    [InlineData(TransferState.Running, TransferState.Paused, true)]
    [InlineData(TransferState.Paused, TransferState.Queued, true)]
    [InlineData(TransferState.Failed, TransferState.Queued, true)]
    [InlineData(TransferState.Completed, TransferState.Queued, false)]
    [InlineData(TransferState.Completed, TransferState.Running, false)]
    [InlineData(TransferState.Queued, TransferState.Completed, false)]
    [InlineData(TransferState.Paused, TransferState.Running, false)]
    public void Transitions(TransferState a, TransferState b, bool ok) => Assert.Equal(ok, TransferStateMachine.CanTransition(a, b));

    [Fact] public void InvalidTransitionThrows()
    {
        var j = new TransferJob(TransferKind.Download, "/a", "b");
        Assert.Throws<InvalidOperationException>(() => j.Transition(TransferState.Completed));
    }
}

public class RetryPolicyTests
{
    [Fact] public void BackoffGrowsAndIsCappedWithJitter()
    {
        var p = new RetryPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), new Random(1));
        for (var a = 1; a <= 10; a++)
        {
            var cap = Math.Min(8000, 1000 * Math.Pow(2, a - 1)); var d = p.GetDelay(a).TotalMilliseconds;
            Assert.InRange(d, cap / 2 - 0.001, cap + 0.001);
        }
    }

    [Fact] public void TransientClassification()
    {
        Assert.True(RetryPolicy.IsTransient(new IOException("net")));
        Assert.True(RetryPolicy.IsTransient(new TimeoutException()));
        Assert.False(RetryPolicy.IsTransient(new IOException("x already exists.")));
        Assert.False(RetryPolicy.IsTransient(new FileNotFoundException()));
        Assert.False(RetryPolicy.IsTransient(new InsufficientSpaceException(2, 1)));
        Assert.False(RetryPolicy.IsTransient(new UnsupportedOperationException("a", "b")));
        Assert.False(RetryPolicy.IsTransient(new OperationCanceledException()));
    }
}

public class TransferManagerTests
{
    static async Task WaitState(TransferJob j, TransferState s, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (j.State != s) { if (DateTime.UtcNow > end) throw new TimeoutException($"Job stuck in {j.State}, wanted {s}. {j.Error}"); await Task.Delay(10); }
    }

    static (TestProvider p, string dir, Tmp t) Setup(int bytes = 300_000)
    {
        var t = new Tmp(); var root = Path.Combine(t.Path, "remote"); Directory.CreateDirectory(root);
        var data = new byte[bytes]; new Random(7).NextBytes(data); File.WriteAllBytes(Path.Combine(root, "f.bin"), data);
        var p = new TestProvider(root); p.ConnectAsync(default).Wait();
        return (p, Path.Combine(t.Path, "out"), t);
    }

    static TransferManager Mgr(ICloudStorageProvider p, int retries = 3) =>
        new(p, 1, 1, new RetryPolicy(retries, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)), (_, _) => Task.CompletedTask);

    [Fact] public async Task DownloadCompletesWithIdenticalContent()
    {
        var (p, dir, t) = Setup(); using var _ = t; await using var m = Mgr(p);
        var j = m.EnqueueDownload("/f.bin", Path.Combine(dir, "f.bin")); await WaitState(j, TransferState.Completed);
        Assert.Equal(File.ReadAllBytes(Path.Combine(t.Path, "remote", "f.bin")), File.ReadAllBytes(Path.Combine(dir, "f.bin")));
        Assert.Equal(100, j.Percent);
        Assert.Empty(Directory.GetFiles(dir, "*.tdpart"));
    }

    [Fact] public async Task TransientFailuresAreRetried()
    {
        var (p, dir, t) = Setup(1000); using var _ = t; p.FailOpenTimes = 2; await using var m = Mgr(p);
        var j = m.EnqueueDownload("/f.bin", Path.Combine(dir, "f.bin")); await WaitState(j, TransferState.Completed);
        Assert.Equal(3, j.Attempts);
    }

    [Fact] public async Task RetriesAreBoundedThenFailWithReadableError()
    {
        var (p, dir, t) = Setup(1000); using var _ = t; p.FailOpenTimes = 100; await using var m = Mgr(p, 2);
        var j = m.EnqueueDownload("/f.bin", Path.Combine(dir, "f.bin")); await WaitState(j, TransferState.Failed);
        Assert.Equal(3, j.Attempts); Assert.NotNull(j.Error);
        p.FailOpenTimes = 0; Assert.True(m.Retry(j)); await WaitState(j, TransferState.Completed);
    }

    [Fact] public async Task ExistingDestinationIsNeverOverwritten()
    {
        var (p, dir, t) = Setup(1000); using var _ = t; Directory.CreateDirectory(dir); var dest = Path.Combine(dir, "f.bin"); File.WriteAllText(dest, "precious");
        await using var m = Mgr(p);
        var j = m.EnqueueDownload("/f.bin", dest); await WaitState(j, TransferState.Failed);
        Assert.Equal("precious", File.ReadAllText(dest)); Assert.Contains("already exists", j.Error);
    }

    [Fact] public async Task CancellationDuringTransferLeavesNoPartialFile()
    {
        var (p, dir, t) = Setup(1000); using var _ = t; p.BlockFirstOpen = true; await using var m = Mgr(p);
        var j = m.EnqueueDownload("/f.bin", Path.Combine(dir, "f.bin")); await WaitState(j, TransferState.Running);
        m.Cancel(j); await WaitState(j, TransferState.Cancelled);
        Assert.False(File.Exists(Path.Combine(dir, "f.bin")));
        if (Directory.Exists(dir)) Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact] public async Task PauseRestartsFromScratchAndProducesCorrectFile()
    {
        var (p, dir, t) = Setup(); using var _ = t; p.BlockFirstOpen = true; await using var m = Mgr(p);
        var j = m.EnqueueDownload("/f.bin", Path.Combine(dir, "f.bin")); await WaitState(j, TransferState.Running);
        m.Pause(j); await WaitState(j, TransferState.Paused);
        Assert.True(m.Resume(j)); await WaitState(j, TransferState.Completed);
        Assert.Equal(File.ReadAllBytes(Path.Combine(t.Path, "remote", "f.bin")), File.ReadAllBytes(Path.Combine(dir, "f.bin")));
    }

    [Fact] public async Task UploadWorksAndRefusesOverwrite()
    {
        var (p, dir, t) = Setup(); using var _ = t; Directory.CreateDirectory(dir); var local = Path.Combine(dir, "u.bin"); File.WriteAllBytes(local, new byte[5000]);
        await using var m = Mgr(p);
        var j = m.EnqueueUpload(local, "/u.bin"); await WaitState(j, TransferState.Completed);
        var j2 = m.EnqueueUpload(local, "/u.bin"); await WaitState(j2, TransferState.Failed);
    }

    [Fact] public async Task UnsupportedProviderRejectsEnqueue()
    {
        await using var m = new TransferManager(new TeraBoxProvider(), 1, 1);
        Assert.Throws<UnsupportedOperationException>(() => m.EnqueueDownload("/x", "y"));
    }

    [Fact] public async Task DisposeDuringActiveTransferDoesNotHang()
    {
        var (p, dir, t) = Setup(1000); using var _ = t; p.BlockFirstOpen = true; var m = Mgr(p);
        var j = m.EnqueueDownload("/f.bin", Path.Combine(dir, "f.bin")); await WaitState(j, TransferState.Running);
        var d = m.DisposeAsync().AsTask(); Assert.Same(d, await Task.WhenAny(d, Task.Delay(8000)));
    }

    [Fact] public async Task CachedFileServiceFetchesOnceAndValidates()
    {
        var (p, _, t) = Setup(5000); using var _ = t;
        using var cache = new CacheManager(Path.Combine(t.Path, "cache"), 1_000_000); var svc = new CachedFileService(cache);
        using (var l = await svc.EnsureCachedAsync(p, "/f.bin", default)) Assert.Equal(5000, new FileInfo(l.Path).Length);
        using (await svc.EnsureCachedAsync(p, "/f.bin", default)) { }
        Assert.Equal(1, p.OpenCalls);
    }

    [Fact] public void SpeedMeterMeasuresObservedBytes()
    {
        var m = new SpeedMeter(5); Assert.Equal(0, m.BytesPerSecond);
        m.Add(1000); Thread.Sleep(300); m.Add(1000);
        Assert.True(m.BytesPerSecond > 0);
    }
}
