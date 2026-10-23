namespace TeraDrive.Core;

/// <summary>Provider-neutral path helpers. Paths use '/', root is "/".</summary>
public static class CloudPath
{
    static readonly char[] InvalidNameChars = "<>:\"|?*\\/".ToCharArray();

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        if (path.Contains('\\')) throw new ArgumentException("Backslashes are not allowed in cloud paths.", nameof(path));
        var parts = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") throw new ArgumentException("Path traversal ('..') is not allowed.", nameof(path));
            ValidateName(seg);
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name must not be empty.");
        if (name.Length > 255) throw new ArgumentException("Name is longer than 255 characters.");
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException($"Name '{name}' contains invalid characters.");
        if (name.TrimEnd() != name || name.EndsWith('.')) throw new ArgumentException($"Name '{name}' must not end with a space or dot.");
    }

    public static string Combine(string parent, string name)
    {
        ValidateName(name);
        var p = Normalize(parent);
        return p == "/" ? "/" + name : p + "/" + name;
    }

    public static string Parent(string path)
    {
        var p = Normalize(path);
        if (p == "/") return "/";
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    public static string NameOf(string path)
    {
        var p = Normalize(path);
        return p == "/" ? "" : p[(p.LastIndexOf('/') + 1)..];
    }
}
