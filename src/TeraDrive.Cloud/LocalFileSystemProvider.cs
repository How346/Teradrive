using TeraDrive.Core;

namespace TeraDrive.Cloud;

/// <summary>
/// Test/demo provider backed by a local folder. This is NOT a cloud service and is always labelled as local.
/// All paths are confined to the configured root.
/// </summary>
public sealed class LocalFileSystemProvider : ICloudStorageProvider
{
    const int BufferSize = 81920;
    readonly string _root;
    volatile bool _connected;

    public LocalFileSystemProvider(string rootDirectory) { _root = Path.GetFullPath(rootDirectory); }

    public string ProviderId => "local";
    public string DisplayName => "Local folder (test provider)";
    public bool IsConnected => _connected;
    public string Root => _root;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.List | ProviderCapabilities.Metadata | ProviderCapabilities.Read | ProviderCapabilities.RangeRead |
        ProviderCapabilities.Upload | ProviderCapabilities.CreateDirectory | ProviderCapabilities.Move |
        ProviderCapabilities.Rename | ProviderCapabilities.Delete;

    public Task ConnectAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct) { _connected = false; return Task.CompletedTask; }

    void EnsureConnected() { if (!_connected) throw new NotConnectedException(ProviderId); }
    string Resolve(string path) { EnsureConnected(); return PathGuard.ResolveUnder(_root, path); }

    public Task<AccountInfo> GetAccountInfoAsync(CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult(new AccountInfo(ProviderId, "Local folder: " + _root));
    }

    CloudItem ToItem(FileSystemInfo fi)
    {
        var rel = Path.GetRelativePath(_root, fi.FullName).Replace(Path.DirectorySeparatorChar, '/');
        var path = rel == "." ? "/" : "/" + rel;
        if (fi is FileInfo f)
            return new CloudItem(path, f.Name, CloudItemKind.File, f.Length, f.LastWriteTimeUtc, $"{f.LastWriteTimeUtc.Ticks}-{f.Length}");
        return new CloudItem(path, fi.Name, CloudItemKind.Directory, 0, fi.LastWriteTimeUtc, null);
    }

    FileSystemInfo Info(string full)
    {
        if (File.Exists(full)) return new FileInfo(full);
        if (Directory.Exists(full)) return new DirectoryInfo(full);
        throw new FileNotFoundException("Item not found.", full);
    }

    public Task<IReadOnlyList<CloudItem>> ListDirectoryAsync(string path, CancellationToken ct)
    {
        var dir = Resolve(path);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Folder not found: " + path);
        var items = new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Where(i => !i.Name.StartsWith(".td-upload-", StringComparison.Ordinal))
            .Select(ToItem)
            .OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<CloudItem>>(items);
    }

    public Task<CloudItem> GetMetadataAsync(string path, CancellationToken ct) => Task.FromResult(ToItem(Info(Resolve(path))));

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) throw new FileNotFoundException("File not found.", path);
        var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset < 0 || offset > fs.Length) { fs.Dispose(); throw new ArgumentOutOfRangeException(nameof(offset)); }
        fs.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(fs);
    }

    public async Task<CloudItem> UploadAsync(string path, Stream content, bool overwrite, Action<long>? onProgress, CancellationToken ct)
    {
        var full = Resolve(path);
        if (full == _root) throw new ArgumentException("Cannot upload to the root.");
        if (Directory.Exists(full)) throw new IOException("A folder with that name already exists.");
        if (File.Exists(full) && !overwrite) throw new IOException($"'{path}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, ".td-upload-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buf = new byte[BufferSize];
                long total = 0; int n;
                while ((n = await content.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    total += n; onProgress?.Invoke(total);
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, full, overwrite);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        return ToItem(new FileInfo(full));
    }

    public Task<CloudItem> CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException($"'{path}' already exists.");
        Directory.CreateDirectory(full);
        return Task.FromResult(ToItem(new DirectoryInfo(full)));
    }

    public Task<CloudItem> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct)
    {
        var src = Resolve(sourcePath); var dst = Resolve(destinationPath);
        if (src == _root || dst == _root) throw new ArgumentException("Cannot move the root.");
        var isDir = Directory.Exists(src);
        if (!isDir && !File.Exists(src)) throw new FileNotFoundException("Source not found.", sourcePath);
        if (File.Exists(dst) || Directory.Exists(dst))
        {
            if (!overwrite || isDir || Directory.Exists(dst)) throw new IOException($"'{destinationPath}' already exists.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (isDir) Directory.Move(src, dst); else File.Move(src, dst, overwrite);
        return Task.FromResult(ToItem(Info(dst)));
    }

    public Task<CloudItem> RenameAsync(string path, string newName, CancellationToken ct)
    {
        CloudPath.ValidateName(newName);
        return MoveAsync(path, CloudPath.Combine(CloudPath.Parent(path), newName), false, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (full == _root) throw new ArgumentException("Cannot delete the root.");
        if (File.Exists(full)) File.Delete(full);
        else if (Directory.Exists(full)) Directory.Delete(full, true);
        else throw new FileNotFoundException("Item not found.", path);
        return Task.CompletedTask;
    }

    public Task<QuotaInfo> GetQuotaAsync(CancellationToken ct) =>
        throw new UnsupportedOperationException("GetQuota", ProviderId, "A local folder has no account quota.");
}
