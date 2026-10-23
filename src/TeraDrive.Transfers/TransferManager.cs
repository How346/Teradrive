using System.Threading.Channels;
using TeraDrive.Core;

namespace TeraDrive.Transfers;

/// <summary>
/// Bounded-queue transfer manager with separate upload/download worker pools.
/// Pause/resume and retry restart the transfer from byte 0 (safe default: no partial-file append is ever attempted).
/// Downloads go to a temporary ".tdpart" file and are moved into place only when complete; existing files are never overwritten
/// unless the job says so. Transfer history is held in memory only.
/// </summary>
public sealed class TransferManager : IAsyncDisposable
{
    const int Buffer = 81920, QueueCapacity = 1000;
    readonly ICloudStorageProvider _provider;
    readonly RetryPolicy _retry;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Channel<TransferJob> _up = Channel.CreateBounded<TransferJob>(QueueCapacity);
    readonly Channel<TransferJob> _down = Channel.CreateBounded<TransferJob>(QueueCapacity);
    readonly List<TransferJob> _jobs = new();
    readonly object _lock = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly List<Task> _workers = new();

    public event Action<TransferJob>? JobAdded;

    public TransferManager(ICloudStorageProvider provider, int uploadConcurrency, int downloadConcurrency, RetryPolicy? retry = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (uploadConcurrency < 1 || downloadConcurrency < 1) throw new ArgumentOutOfRangeException("Concurrency must be >= 1.");
        _provider = provider; _retry = retry ?? new RetryPolicy(); _delay = delay ?? ((t, c) => Task.Delay(t, c));
        for (var i = 0; i < uploadConcurrency; i++) _workers.Add(Task.Run(() => Worker(_up.Reader)));
        for (var i = 0; i < downloadConcurrency; i++) _workers.Add(Task.Run(() => Worker(_down.Reader)));
    }

    public IReadOnlyList<TransferJob> Jobs { get { lock (_lock) return _jobs.ToList(); } }

    public TransferJob EnqueueDownload(string remotePath, string localPath, bool overwrite = false) => Add(new TransferJob(TransferKind.Download, remotePath, localPath, overwrite));
    public TransferJob EnqueueUpload(string localPath, string remotePath, bool overwrite = false) => Add(new TransferJob(TransferKind.Upload, remotePath, localPath, overwrite));

    TransferJob Add(TransferJob job)
    {
        _provider.Require(job.Kind == TransferKind.Upload ? ProviderCapabilities.Upload : ProviderCapabilities.Read, job.Kind.ToString());
        lock (_lock) _jobs.Add(job);
        if (!Queue(job)) { lock (_lock) _jobs.Remove(job); throw new InvalidOperationException("The transfer queue is full."); }
        JobAdded?.Invoke(job);
        return job;
    }

    bool Queue(TransferJob j) => (j.Kind == TransferKind.Upload ? _up : _down).Writer.TryWrite(j);

    public void Pause(TransferJob job)
    {
        if (job.State == TransferState.Queued) { job.TryTransition(TransferState.Paused); return; }
        if (job.State == TransferState.Running) { job.PauseRequested = true; SafeCancel(job); }
    }

    public bool Resume(TransferJob job) => job.State == TransferState.Paused && job.TryTransition(TransferState.Queued) && Queue(job);

    public void Cancel(TransferJob job)
    {
        if (job.State is TransferState.Queued or TransferState.Paused) job.TryTransition(TransferState.Cancelled);
        else if (job.State == TransferState.Running) { job.PauseRequested = false; SafeCancel(job); }
    }

    static void SafeCancel(TransferJob job) { try { job.Cts?.Cancel(); } catch (ObjectDisposedException) { } }

    public bool Retry(TransferJob job)
    {
        if (job.State is not (TransferState.Failed or TransferState.Cancelled)) return false;
        job.Error = null; job.ResetProgress();
        return job.TryTransition(TransferState.Queued) && Queue(job);
    }

    public void PauseAll() { foreach (var j in Jobs) Pause(j); }
    public void ResumeAll() { foreach (var j in Jobs.Where(j => j.State == TransferState.Paused)) Resume(j); }
    public int ClearFinished() { lock (_lock) return _jobs.RemoveAll(j => TransferStateMachine.IsFinished(j.State)); }

    public (int activeUploads, int activeDownloads, double upBps, double downBps, int failed) Snapshot()
    {
        var js = Jobs; var run = js.Where(j => j.State == TransferState.Running).ToList();
        return (run.Count(j => j.Kind == TransferKind.Upload), run.Count(j => j.Kind == TransferKind.Download),
            run.Where(j => j.Kind == TransferKind.Upload).Sum(j => j.BytesPerSecond),
            run.Where(j => j.Kind == TransferKind.Download).Sum(j => j.BytesPerSecond),
            js.Count(j => j.State == TransferState.Failed));
    }

    async Task Worker(ChannelReader<TransferJob> reader)
    {
        try { await foreach (var job in reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false)) await RunAsync(job).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    async Task RunAsync(TransferJob job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        job.Cts = cts; job.PauseRequested = false;
        if (!job.TryTransition(TransferState.Running)) return; // paused/cancelled while queued
        job.ResetProgress();
        var attempt = 0;
        while (true)
        {
            attempt++; job.IncrementAttempts(); job.ResetProgress();
            try
            {
                if (job.Kind == TransferKind.Download) await DownloadAsync(job, cts.Token).ConfigureAwait(false);
                else await UploadAsync(job, cts.Token).ConfigureAwait(false);
                job.TryTransition(TransferState.Completed);
                return;
            }
            catch (OperationCanceledException)
            {
                job.TryTransition(job.PauseRequested ? TransferState.Paused : TransferState.Cancelled);
                return;
            }
            catch (Exception ex) when (attempt <= _retry.MaxRetries && RetryPolicy.IsTransient(ex))
            {
                try { await _delay(_retry.GetDelay(attempt), cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { job.TryTransition(job.PauseRequested ? TransferState.Paused : TransferState.Cancelled); return; }
            }
            catch (Exception ex)
            {
                job.Error = ErrorTranslator.Describe(ex);
                job.TryTransition(TransferState.Failed);
                return;
            }
        }
    }

    static void EnsureSpace(string destination, long needed)
    {
        if (needed <= 0) return;
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return; }
        if (free < needed) throw new InsufficientSpaceException(needed, free);
    }

    async Task DownloadAsync(TransferJob job, CancellationToken ct)
    {
        var dest = Path.GetFullPath(job.LocalPath);
        if (File.Exists(dest) && !job.Overwrite) throw new IOException($"'{dest}' already exists.");
        var meta = await _provider.GetMetadataAsync(job.RemotePath, ct).ConfigureAwait(false);
        if (meta.IsDirectory) throw new ArgumentException("Cannot download a folder as a file.");
        job.SetTotal(meta.Size);
        EnsureSpace(dest, meta.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + "." + job.Id.ToString("N")[..8] + ".tdpart";
        try
        {
            await using (var src = await _provider.OpenReadAsync(job.RemotePath, 0, ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, Buffer, true))
            {
                var buf = new byte[Buffer]; long total = 0; int n;
                while ((n = await src.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    total += n; job.SetBytes(total);
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
                if (meta.Size >= 0 && total != meta.Size) throw new IOException($"Incomplete download: expected {meta.Size} bytes, got {total}.");
            }
            File.Move(part, dest, job.Overwrite);
        }
        finally { try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } }
    }

    async Task UploadAsync(TransferJob job, CancellationToken ct)
    {
        await using var fs = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, true);
        job.SetTotal(fs.Length);
        await _provider.UploadAsync(job.RemotePath, fs, job.Overwrite, job.SetBytes, ct).ConfigureAwait(false);
        job.SetBytes(fs.Length);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _up.Writer.TryComplete(); _down.Writer.TryComplete();
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
