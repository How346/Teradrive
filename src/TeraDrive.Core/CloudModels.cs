namespace TeraDrive.Core;

[Flags]
public enum ProviderCapabilities
{
    None = 0, List = 1, Metadata = 2, Read = 4, RangeRead = 8, Upload = 16,
    CreateDirectory = 32, Move = 64, Rename = 128, Delete = 256, Quota = 512,
}

public enum CloudItemKind { File, Directory }

/// <param name="Path">Normalized provider path, '/'-separated, root is "/".</param>
/// <param name="Version">Opaque change identifier (e.g. etag/mtime+size) used for cache invalidation.</param>
public sealed record CloudItem(string Path, string Name, CloudItemKind Kind, long Size, DateTimeOffset? Modified, string? Version)
{
    public bool IsDirectory => Kind == CloudItemKind.Directory;
}

public sealed record QuotaInfo(long TotalBytes, long UsedBytes);
public sealed record AccountInfo(string ProviderId, string DisplayName);

public interface ICloudStorageProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<AccountInfo> GetAccountInfoAsync(CancellationToken ct);
    Task<IReadOnlyList<CloudItem>> ListDirectoryAsync(string path, CancellationToken ct);
    Task<CloudItem> GetMetadataAsync(string path, CancellationToken ct);
    /// <summary>Opens a read stream starting at <paramref name="offset"/>. Offsets &gt; 0 require RangeRead.</summary>
    Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct);
    Task<CloudItem> UploadAsync(string path, Stream content, bool overwrite, Action<long>? onProgress, CancellationToken ct);
    Task<CloudItem> CreateDirectoryAsync(string path, CancellationToken ct);
    Task<CloudItem> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct);
    Task<CloudItem> RenameAsync(string path, string newName, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task<QuotaInfo> GetQuotaAsync(CancellationToken ct);
}

public static class ProviderExtensions
{
    public static bool Supports(this ICloudStorageProvider p, ProviderCapabilities c) => (p.Capabilities & c) == c;

    public static void Require(this ICloudStorageProvider p, ProviderCapabilities c, string operation)
    {
        if (!p.Supports(c)) throw new UnsupportedOperationException(operation, p.ProviderId);
    }

    public static string Describe(this ProviderCapabilities c) => c == ProviderCapabilities.None ? "none" : c.ToString();
}
