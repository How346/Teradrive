using System.ComponentModel;
using System.Diagnostics;

namespace TeraDrive.Transfers;

public enum TransferKind { Upload, Download }
public enum TransferState { Queued, Running, Paused, Completed, Failed, Cancelled }

public static class TransferStateMachine
{
    public static bool CanTransition(TransferState from, TransferState to) => (from, to) switch
    {
        (TransferState.Queued, TransferState.Running or TransferState.Paused or TransferState.Cancelled) => true,
        (TransferState.Running, TransferState.Completed or TransferState.Failed or TransferState.Cancelled or TransferState.Paused) => true,
        (TransferState.Paused, TransferState.Queued or TransferState.Cancelled) => true,
        (TransferState.Failed, TransferState.Queued) => true,
        (TransferState.Cancelled, TransferState.Queued) => true,
        _ => false,
    };

    public static bool IsFinished(TransferState s) => s is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;
}

/// <summary>Rolling-window throughput meter based on bytes actually observed.</summary>
public sealed class SpeedMeter
{
    readonly object _l = new();
    readonly Queue<(long ts, long total)> _samples = new();
    readonly long _windowTicks;
    long _total;
    public SpeedMeter(double windowSeconds = 5) { _windowTicks = (long)(windowSeconds * Stopwatch.Frequency); }

    public void Add(long bytes)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_l) { _total += bytes; _samples.Enqueue((now, _total)); Trim(now); }
    }

    void Trim(long now) { while (_samples.Count > 0 && now - _samples.Peek().ts > _windowTicks) _samples.Dequeue(); }

    public double BytesPerSecond
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            lock (_l)
            {
                Trim(now);
                if (_samples.Count == 0) return 0;
                var first = _samples.Peek();
                var dt = (now - first.ts) / (double)Stopwatch.Frequency;
                return dt <= 0.05 ? 0 : (_total - first.total) / dt;
            }
        }
    }
}

public sealed class TransferJob : INotifyPropertyChanged
{
    readonly object _gate = new();
    TransferState _state = TransferState.Queued;
    long _bytes, _total = -1, _lastRaise;
    string? _error; int _attempts;
    long _startTs;

    public TransferJob(TransferKind kind, string remotePath, string localPath, bool overwrite = false)
    { Kind = kind; RemotePath = remotePath; LocalPath = localPath; Overwrite = overwrite; CreatedAt = DateTimeOffset.Now; }

    public Guid Id { get; } = Guid.NewGuid();
    public TransferKind Kind { get; }
    public string RemotePath { get; }
    public string LocalPath { get; }
    public bool Overwrite { get; }
    public DateTimeOffset CreatedAt { get; }
    internal SpeedMeter Speed { get; private set; } = new();
    internal CancellationTokenSource? Cts { get; set; }
    internal volatile bool PauseRequested;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }

    public TransferState State { get { lock (_gate) return _state; } }
    public long BytesTransferred => Interlocked.Read(ref _bytes);
    public long TotalBytes => Interlocked.Read(ref _total);
    public string? Error { get { lock (_gate) return _error; } internal set { lock (_gate) _error = value; Raise(nameof(Error)); } }
    public int Attempts { get { lock (_gate) return _attempts; } }
    internal void IncrementAttempts() { lock (_gate) _attempts++; }
    public string Name => System.IO.Path.GetFileName(Kind == TransferKind.Download ? RemotePath : LocalPath);
    public double Percent => TotalBytes > 0 ? Math.Min(100.0, BytesTransferred * 100.0 / TotalBytes) : 0;
    public double BytesPerSecond => State == TransferState.Running ? Speed.BytesPerSecond : 0;
    public string SpeedText => BytesPerSecond > 0 ? Format(BytesPerSecond) + "/s" : "";
    public string EtaText
    {
        get
        {
            var s = BytesPerSecond; var rem = TotalBytes - BytesTransferred;
            if (s < 1 || TotalBytes <= 0 || rem < 0) return "";
            return TimeSpan.FromSeconds(rem / s).ToString(@"hh\:mm\:ss");
        }
    }
    public string StateText => State.ToString();

    public static string Format(double bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; var i = 0;
        while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {u[i]}";
    }

    public bool TryTransition(TransferState next)
    {
        lock (_gate)
        {
            if (!TransferStateMachine.CanTransition(_state, next)) return false;
            _state = next;
        }
        Raise(nameof(State), nameof(StateText), nameof(SpeedText), nameof(EtaText));
        return true;
    }

    public void Transition(TransferState next)
    {
        if (!TryTransition(next)) throw new InvalidOperationException($"Invalid transfer transition {State} -> {next}.");
    }

    internal void ResetProgress()
    {
        Interlocked.Exchange(ref _bytes, 0); Speed = new SpeedMeter(); _startTs = Stopwatch.GetTimestamp();
        Raise(nameof(BytesTransferred), nameof(Percent));
    }

    internal void SetTotal(long total) { Interlocked.Exchange(ref _total, total); Raise(nameof(TotalBytes)); }

    internal void SetBytes(long absolute)
    {
        var delta = absolute - Interlocked.Exchange(ref _bytes, absolute);
        if (delta > 0) Speed.Add(delta);
        Throttled();
    }

    void Throttled()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastRaise) < Stopwatch.Frequency / 5) return;
        Interlocked.Exchange(ref _lastRaise, now);
        Raise(nameof(BytesTransferred), nameof(Percent), nameof(SpeedText), nameof(EtaText));
    }
}
