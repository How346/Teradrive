using System.Text.Json;

namespace TeraDrive.Media;

public static class VideoFiles
{
    // Container extensions only. Actual playback depends on codecs installed in Windows (WPF MediaElement / Media Foundation).
    static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".ts" };
    public static bool IsVideo(string fileName) => Ext.Contains(Path.GetExtension(fileName));
}

/// <summary>Remembers playback positions per file key (JSON, atomic write).</summary>
public sealed class PlaybackPositionStore
{
    readonly string _path; readonly object _l = new();
    readonly Dictionary<string, PositionRecord> _map;
    public sealed class PositionRecord { public double Seconds { get; set; } public DateTimeOffset LastPlayed { get; set; } }

    public PlaybackPositionStore(string path)
    {
        _path = path; _map = new();
        try { if (File.Exists(path)) _map = JsonSerializer.Deserialize<Dictionary<string, PositionRecord>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { _map = new(); }
    }

    public double GetPosition(string key) { lock (_l) return _map.TryGetValue(key, out var r) ? r.Seconds : 0; }

    public void Save(string key, double seconds)
    {
        lock (_l)
        {
            _map[key] = new PositionRecord { Seconds = Math.Max(0, seconds), LastPlayed = DateTimeOffset.UtcNow };
            if (_map.Count > 500) foreach (var k in _map.OrderBy(kv => kv.Value.LastPlayed).Take(_map.Count - 500).Select(kv => kv.Key).ToList()) _map.Remove(k);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(_map)); File.Move(tmp, _path, true);
        }
    }

    public IReadOnlyList<string> RecentKeys(int n) { lock (_l) return _map.OrderByDescending(kv => kv.Value.LastPlayed).Take(n).Select(kv => kv.Key).ToList(); }
}
