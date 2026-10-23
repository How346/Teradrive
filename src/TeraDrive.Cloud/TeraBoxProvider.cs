using TeraDrive.Core;

namespace TeraDrive.Cloud;

/// <summary>
/// Placeholder that honestly reports TeraBox as UNAVAILABLE. No officially documented, supported personal-drive API
/// (list/read/upload with a sanctioned sign-in flow) could be verified, and the only integrations found rely on
/// reverse-engineered endpoints and browser cookies/tokens, which this project deliberately does not use.
/// Every operation throws <see cref="UnsupportedOperationException"/>; nothing returns fake success.
/// See docs/TERABOX-INTEGRATION.md.
/// </summary>
public sealed class TeraBoxProvider : ICloudStorageProvider
{
    const string Reason = "No supported TeraBox personal-drive API is available to this build. See docs/TERABOX-INTEGRATION.md.";

    public string ProviderId => "terabox";
    public string DisplayName => "TeraBox";
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;
    public bool IsConnected => false;

    static Exception Unsupported(string op) => new UnsupportedOperationException(op, "terabox", Reason);

    public Task ConnectAsync(CancellationToken ct) => throw Unsupported("Connect");
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask; // nothing is ever connected
    public Task<AccountInfo> GetAccountInfoAsync(CancellationToken ct) => throw Unsupported("GetAccountInfo");
    public Task<IReadOnlyList<CloudItem>> ListDirectoryAsync(string path, CancellationToken ct) => throw Unsupported("ListDirectory");
    public Task<CloudItem> GetMetadataAsync(string path, CancellationToken ct) => throw Unsupported("GetMetadata");
    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct) => throw Unsupported("OpenRead");
    public Task<CloudItem> UploadAsync(string path, Stream content, bool overwrite, Action<long>? onProgress, CancellationToken ct) => throw Unsupported("Upload");
    public Task<CloudItem> CreateDirectoryAsync(string path, CancellationToken ct) => throw Unsupported("CreateDirectory");
    public Task<CloudItem> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct) => throw Unsupported("Move");
    public Task<CloudItem> RenameAsync(string path, string newName, CancellationToken ct) => throw Unsupported("Rename");
    public Task DeleteAsync(string path, CancellationToken ct) => throw Unsupported("Delete");
    public Task<QuotaInfo> GetQuotaAsync(CancellationToken ct) => throw Unsupported("GetQuota");
}
