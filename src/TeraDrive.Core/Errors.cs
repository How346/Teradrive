namespace TeraDrive.Core;

/// <summary>Thrown when the active provider does not support an operation.</summary>
public sealed class UnsupportedOperationException : NotSupportedException
{
    public string Operation { get; }
    public string ProviderId { get; }
    public UnsupportedOperationException(string operation, string providerId, string? reason = null)
        : base($"'{operation}' is not supported by provider '{providerId}'." + (reason is null ? "" : " " + reason))
    { Operation = operation; ProviderId = providerId; }
}

public sealed class NotConnectedException : InvalidOperationException
{
    public NotConnectedException(string providerId) : base($"Provider '{providerId}' is not connected.") { }
}

public sealed class InsufficientSpaceException : IOException
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
    public InsufficientSpaceException(long required, long available)
        : base($"Not enough free disk space: {required} bytes required, {available} available.")
    { RequiredBytes = required; AvailableBytes = available; }
}

public sealed class CacheLimitException : IOException
{
    public CacheLimitException(string message) : base(message) { }
}
