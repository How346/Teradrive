using System.Diagnostics;
using System.Text.Json;

namespace TeraDrive.Games;

public sealed class GameEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Arguments { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public string InstallPath => string.IsNullOrEmpty(WorkingDirectory) ? (Path.GetDirectoryName(ExecutablePath) ?? "") : WorkingDirectory;
}

/// <summary>Launch-shortcut library for locally installed games. Removing an entry never touches game files.</summary>
public sealed class GameLibrary
{
    readonly string _path; readonly object _l = new(); List<GameEntry> _games = new();
    public GameLibrary(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _games = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { _games = new(); }
    }

    public IReadOnlyList<GameEntry> All() { lock (_l) return _games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(); }

    public GameEntry Add(string name, string exePath, string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.");
        if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath)) throw new FileNotFoundException("Executable not found.", exePath);
        var g = new GameEntry { Name = name.Trim(), ExecutablePath = Path.GetFullPath(exePath), Arguments = arguments };
        lock (_l) { _games.Add(g); Save(); }
        return g;
    }

    /// <summary>Removes the shortcut only; installed files are never deleted.</summary>
    public bool Remove(Guid id) { lock (_l) { var n = _games.RemoveAll(g => g.Id == id); if (n > 0) Save(); return n > 0; } }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(_games, new JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, _path, true);
    }

    /// <summary>Total size of the install folder; enumeration is capped to keep the UI responsive.</summary>
    public static Task<long> ComputeInstallSizeAsync(GameEntry g, CancellationToken ct, int maxFiles = 200_000) => Task.Run(() =>
    {
        long total = 0; var count = 0;
        if (!Directory.Exists(g.InstallPath)) return 0L;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(g.InstallPath).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            total += f.Length;
            if (++count >= maxFiles) break;
        }
        return total;
    }, ct);
}

public static class GameLauncher
{
    /// <summary>Starts the game executable normally. No DRM/launcher/anti-cheat interaction of any kind.</summary>
    public static Process? Launch(GameEntry g)
    {
        if (!File.Exists(g.ExecutablePath)) throw new FileNotFoundException("The game executable no longer exists. Remove the shortcut or re-add it.", g.ExecutablePath);
        var psi = new ProcessStartInfo(g.ExecutablePath, g.Arguments) { WorkingDirectory = g.InstallPath, UseShellExecute = true };
        return Process.Start(psi);
    }
}
