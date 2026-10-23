using System.Net.Http;
using TeraDrive.Core;

namespace TeraDrive.Transfers;

public sealed class RetryPolicy
{
    public int MaxRetries { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }
    readonly Random _rng;

    public RetryPolicy(int maxRetries = 4, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, Random? rng = null)
    {
        MaxRetries = maxRetries;
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
        _rng = rng ?? Random.Shared;
    }

    /// <summary>Exponential backoff with "equal jitter": delay in [cap/2, cap] where cap = min(max, base*2^(attempt-1)).</summary>
    public TimeSpan GetDelay(int attempt)
    {
        var exp = Math.Min(MaxDelay.TotalMilliseconds, BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds(exp / 2 + _rng.NextDouble() * exp / 2);
    }

    public static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        UnsupportedOperationException or NotConnectedException or InsufficientSpaceException or CacheLimitException => false,
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or ArgumentException => false,
        HttpRequestException or TimeoutException => true,
        IOException io => !io.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
