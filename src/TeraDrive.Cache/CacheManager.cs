using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeraDrive.Core;

namespace TeraDrive.Cache;

public sealed class CacheEntry
{
    public string Key { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? Version { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Pinned { get; set; }
    public int AccessCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastAccess { get; set; }
    public CacheEntry Clone() => (CacheEntry)MemberwiseClone();
}

/// <summary>Keeps a cached file safe from eviction while it is being read. Dispose when done.</summary>
public sealed class CacheLease : IDisposable
{
    readonly CacheManager _owner; readonly string _key; int _disposed;
    internal CacheLease(CacheManager owner, string key, string path) { _owner = owner; _key = key; Path = path; }
    public string Path { get; }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(_key); }
}

/// <summary>
/// Local content cache: temp downloads are kept separate and promoted atomically after size + SHA-256 verification.
/// LRU eviction never touches pinned or in-use entries. Index is JSON (SQLite was deliberately avoided to keep the
/// dependency set empty; see docs/ARCHITECTURE.md). Thread-safe.
/// </summary>
public sealed class CacheManager : IDisposable
{
    const long MinFreeBytes = 64L * 1024 * 1024;
    sealed class IndexFile { public int Version { get; set; } = 1; public List<CacheEntry> Entries { get; set; } = new(); }

    readonly object _gate = new();
    readonly string _root, _objects, _tmp, _indexPath;
    readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _inUse = new(StringComparer.Ordinal);
    readonly Dictionary<string, Task<CacheEntry>> _inflight = new(StringComparer.Ordinal);
    long _limit; bool _dirty;

    public CacheManager(string rootDirectory, long limitBytes)
    {
        if (limitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(limitBytes));
        _limit = limitBytes;
        _root = Path.GetFullPath(rootDirectory);
        _objects = Path.Combine(_root, "objects");
        _tmp = Path.Combine(_root, "tmp");
        _indexPath = Path.Combine(_root, "index.json");
        Directory.CreateDirectory(_objects); Directory.CreateDirectory(_tmp);
        Load(); Recover();
    }

    public string RootDirectory => _root;
    public long LimitBytes { get { lock (_gate) return _limit; } }
    public long UsedBytes { get { lock (_gate) return UsedLocked(); } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public IReadOnlyList<CacheEntry> Entries() { lock (_gate) return _entries.Values.Select(e => e.Clone()).OrderByDescending(e => e.LastAccess).ToList(); }
    public CacheEntry? Peek(string key) { lock (_gate) return _entries.TryGetValue(key, out var e) ? e.Clone() : null; }
    public string NewTempPath() => Path.Combine(_tmp, Guid.NewGuid().ToString("N") + ".part");

    long UsedLocked() => _entries.Values.Sum(e => e.Size);
    bool InUseLocked(string key) => _inUse.TryGetValue(key, out var n) && n > 0;
    static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
    string ObjectPath(CacheEntry e) => Path.Combine(_objects, e.FileName);

    // ---------- persistence ----------
    void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            var idx = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(_indexPath));
            if (idx is null) return;
            foreach (var e in idx.Entries.Where(e => !string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.FileName) && e.FileName == Path.GetFileName(e.FileName)))
                _entries[e.Key] = e;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try { File.Move(_indexPath, _indexPath + ".corrupt", true); } catch (IOException) { }
            _entries.Clear(); // objects without an index entry are removed in Recover()
        }
    }

    void SaveLocked()
    {
        var tmp = _indexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new IndexFile { Entries = _entries.Values.ToList() }));
        File.Move(tmp, _indexPath, true);
        _dirty = false;
    }

    /// <summary>Startup recovery: drop temp files, index entries with missing/mis-sized files, and orphan objects.</summary>
    void Recover()
    {
        foreach (var f in Directory.EnumerateFiles(_tmp)) TryDelete(f);
        lock (_gate)
        {
            foreach (var e in _entries.Values.ToList())
            {
                var p = ObjectPath(e);
                if (!File.Exists(p) || new FileInfo(p).Length != e.Size) _entries.Remove(e.Key);
            }
            var known = _entries.Values.Select(e => e.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(_objects))
                if (!known.Contains(Path.GetFileName(f))) TryDelete(f);
            SaveLocked();
        }
    }

    static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---------- lookup / leases ----------
    /// <summary>Returns a lease if a valid entry exists. A differing version invalidates the entry.</summary>
    public CacheLease? TryGetLease(string key, string providerId, string? version)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e)) return null;
            var stale = e.ProviderId != providerId || (version is not null && e.Version != version);
            var path = ObjectPath(e);
            if (stale || !File.Exists(path))
            {
                RemoveLocked(e); SaveLocked();
                return null;
            }
            e.LastAccess = DateTimeOffset.UtcNow; e.AccessCount++; _dirty = true;
            _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
            return new CacheLease(this, key, path);
        }
    }

    internal void Release(string key)
    {
        lock (_gate)
        {
            if (_inUse.TryGetValue(key, out var n)) { if (n <= 1) _inUse.Remove(key); else _inUse[key] = n - 1; }
            if (_dirty) { try { SaveLocked(); } catch (IOException) { } }
        }
    }

    /// <summary>
    /// Returns a lease for the cached file, fetching it with <paramref name="fetch"/> (writes to the supplied temp path) on a miss.
    /// Concurrent requests for the same key share one fetch. Unknown size: pass a negative expectedSize.
    /// </summary>
    public async Task<CacheLease> GetOrFetchAsync(string key, string providerId, string? version, long expectedSize,
        Func<string, CancellationToken, Task> fetch, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var hit = TryGetLease(key, providerId, version);
            if (hit is not null) return hit;

            Task<CacheEntry> task;
            lock (_gate)
            {
                if (!_inflight.TryGetValue(key, out task!))
                {
                    task = Task.Run(() => FetchAsync(key, providerId, version, expectedSize, fetch, ct), CancellationToken.None);
                    _inflight[key] = task;
                }
            }
            await task.WaitAsync(ct).ConfigureAwait(false);
        }
        throw new IOException("The cache entry was evicted while being prepared. Please retry.");
    }

    async Task<CacheEntry> FetchAsync(string key, string providerId, string? version, long expectedSize,
        Func<string, CancellationToken, Task> fetch, CancellationToken ct)
    {
        var tmp = NewTempPath();
        try
        {
            if (expectedSize > LimitBytes) throw new CacheLimitException("The file is larger than the whole cache limit.");
            EnsureDiskSpace(expectedSize);
            await fetch(tmp, ct).ConfigureAwait(false);
            if (!File.Exists(tmp)) throw new IOException("The download produced no file.");
            var len = new FileInfo(tmp).Length;
            if (expectedSize >= 0 && len != expectedSize)
                throw new IOException($"Incomplete download: expected {expectedSize} bytes, got {len}.");
            return await CommitAsync(key, providerId, version, tmp, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
            lock (_gate) _inflight.Remove(key);
        }
    }

    void EnsureDiskSpace(long expected)
    {
        if (expected <= 0) return;
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return; } // cannot determine; proceed
        if (free < expected + MinFreeBytes) throw new InsufficientSpaceException(expected + MinFreeBytes, free);
    }

    static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    /// <summary>Verifies (SHA-256) and atomically promotes a completed temp file into the cache.</summary>
    public async Task<CacheEntry> CommitAsync(string key, string providerId, string? version, string tempPath, CancellationToken ct)
    {
        var sha = await HashFileAsync(tempPath, ct).ConfigureAwait(false);
        var size = new FileInfo(tempPath).Length;
        lock (_gate)
        {
            if (size > _limit) throw new CacheLimitException("The file is larger than the whole cache limit.");
            var wasPinned = false;
            if (_entries.TryGetValue(key, out var old)) { wasPinned = old.Pinned; RemoveLocked(old); }
            EvictLocked(size);
            var name = HashKey(key) + "-" + Guid.NewGuid().ToString("N")[..8] + ".bin";
            File.Move(tempPath, Path.Combine(_objects, name));
            var now = DateTimeOffset.UtcNow;
            var e = new CacheEntry { Key = key, ProviderId = providerId, Version = version, FileName = name, Size = size, Sha256 = sha,
                Pinned = wasPinned, AccessCount = 0, CreatedAt = now, LastAccess = now };
            _entries[key] = e;
            SaveLocked();
            return e.Clone();
        }
    }

    // ---------- eviction ----------
    bool RemoveLocked(CacheEntry e)
    {
        _entries.Remove(e.Key);
        return TryDelete(ObjectPath(e)); // if still open by a reader, the orphan is cleaned on next startup
    }

    void EvictLocked(long needed)
    {
        var used = UsedLocked();
        if (used + needed <= _limit) return;
        foreach (var e in _entries.Values.Where(e => !e.Pinned && !InUseLocked(e.Key)).OrderBy(e => e.LastAccess).ToList())
        {
            if (used + needed <= _limit) break;
            if (TryDelete(ObjectPath(e))) { _entries.Remove(e.Key); used -= e.Size; }
        }
        if (used + needed > _limit)
            throw new CacheLimitException("Cache limit reached: remaining cached files are pinned or currently in use.");
    }

    public void SetLimit(long limitBytes)
    {
        if (limitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(limitBytes));
        lock (_gate)
        {
            _limit = limitBytes;
            try { EvictLocked(0); } catch (CacheLimitException) { /* pinned/in-use data may exceed limit until released */ }
            SaveLocked();
        }
    }

    // ---------- management ----------
    public void Pin(string key) => SetPinned(key, true);
    public void Unpin(string key) => SetPinned(key, false);
    void SetPinned(string key, bool pinned)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e)) throw new KeyNotFoundException("Item is not cached.");
            e.Pinned = pinned; SaveLocked();
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e) || InUseLocked(key)) return false;
            var ok = RemoveLocked(e); SaveLocked(); return ok;
        }
    }

    /// <summary>Deletes cached copies that are not pinned and not in use. Never touches originals or cloud data.</summary>
    public int ClearUnpinned() => Clear(includePinned: false);
    public int ClearAll() => Clear(includePinned: true);

    int Clear(bool includePinned)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var e in _entries.Values.Where(e => (includePinned || !e.Pinned) && !InUseLocked(e.Key)).ToList())
                if (RemoveLocked(e)) n++;
            SaveLocked();
            return n;
        }
    }

    /// <summary>Re-hashes cached files; corrupt or missing entries are removed. Returns removed keys.</summary>
    public async Task<IReadOnlyList<string>> VerifyIntegrityAsync(CancellationToken ct)
    {
        List<CacheEntry> snapshot;
        lock (_gate) snapshot = _entries.Values.Select(e => e.Clone()).ToList();
        var bad = new List<string>();
        foreach (var e in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_objects, e.FileName);
            string? sha = null;
            try { if (File.Exists(path)) sha = await HashFileAsync(path, ct).ConfigureAwait(false); }
            catch (IOException) { }
            if (!string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase)) bad.Add(e.Key);
        }
        lock (_gate)
        {
            foreach (var k in bad) if (_entries.TryGetValue(k, out var e)) RemoveLocked(e);
            SaveLocked();
        }
        return bad;
    }

    public void Dispose() { lock (_gate) { if (_dirty) { try { SaveLocked(); } catch (IOException) { } } } }
}
