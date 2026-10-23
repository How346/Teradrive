namespace TeraDrive.Core;

public static class PathGuard
{
    static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Maps a provider path to a local path under <paramref name="root"/>; throws if it would escape root.</summary>
    public static string ResolveUnder(string root, string cloudPath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rel = CloudPath.Normalize(cloudPath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel));
        if (!(full.Equals(rootFull, Cmp) || full.StartsWith(rootFull + Path.DirectorySeparatorChar, Cmp)))
            throw new ArgumentException("Path escapes the provider root.");
        return full;
    }

    public static bool IsValidLocalDirectory(string? path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Path is empty."; return false; }
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { error = "Path contains invalid characters."; return false; }
        if (!Path.IsPathFullyQualified(path)) { error = "Path must be absolute."; return false; }
        return true;
    }
}
