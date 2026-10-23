using TeraDrive.Cache;
using TeraDrive.Core;

namespace TeraDrive.Transfers;

/// <summary>Makes a remote file available locally through the cache (full-file fetch; no partial/range streaming).</summary>
public sealed class CachedFileService
{
    readonly CacheManager _cache;
    public CachedFileService(CacheManager cache) { _cache = cache; }

    public static string KeyFor(ICloudStorageProvider p, string path) => p.ProviderId + ":" + CloudPath.Normalize(path);

    public async Task<CacheLease> EnsureCachedAsync(ICloudStorageProvider provider, string remotePath, CancellationToken ct)
    {
        provider.Require(ProviderCapabilities.Read | ProviderCapabilities.Metadata, "Read");
        var meta = await provider.GetMetadataAsync(remotePath, ct).ConfigureAwait(false);
        if (meta.IsDirectory) throw new ArgumentException("A folder cannot be cached as a file.");
        return await _cache.GetOrFetchAsync(KeyFor(provider, meta.Path), provider.ProviderId, meta.Version, meta.Size,
            async (tmp, token) =>
            {
                await using var src = await provider.OpenReadAsync(meta.Path, 0, token).ConfigureAwait(false);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await src.CopyToAsync(dst, 81920, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }
}
