using System.Net.Http;

namespace TeraDrive.Core;

/// <summary>Turns exceptions into short, actionable, secret-free messages (no stack traces).</summary>
public static class ErrorTranslator
{
    const int ErrorDiskFull = unchecked((int)0x80070070);
    const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static string Describe(Exception ex) => ex switch
    {
        UnsupportedOperationException u => $"This action isn't supported by {u.ProviderId}. {u.Message}",
        NotConnectedException => "The cloud provider is not connected. Connect it from Settings and try again.",
        InsufficientSpaceException => "Not enough free disk space. Free some space or choose another location.",
        CacheLimitException => "The cache limit was reached and remaining cached files are pinned or in use. Increase the limit or unpin files.",
        OperationCanceledException => "The operation was cancelled.",
        FileNotFoundException or DirectoryNotFoundException => "The file or folder could not be found. It may have been moved or deleted.",
        UnauthorizedAccessException => "Access was denied. Check permissions or whether the file is in use.",
        HttpRequestException => "A network error occurred. Check your connection and retry.",
        TimeoutException => "The operation timed out. Check your connection and retry.",
        IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => "The disk is full.",
        IOException io when io.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => "A file with that name already exists. Nothing was overwritten.",
        IOException => "A file operation failed: " + Redactor.Redact(ex.Message),
        ArgumentException a => "Invalid input: " + Redactor.Redact(a.Message),
        _ => "An unexpected error occurred (" + ex.GetType().Name + "). See Diagnostics for details.",
    };
}
