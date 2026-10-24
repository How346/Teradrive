using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace TeraDrive.Core;

public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|jstoken|secret|cookie|ndus|authorization|api[_-]?key|refresh[_-]?token|access[_-]?token)\b\s*[=:]\s*(?:Bearer\s+)?[^\s,;&]+")]
    private static partial Regex KeyValue();
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex Bearer();

    public static string Redact(string text)
    {
        text = Bearer().Replace(text, "Bearer [REDACTED]");
        return KeyValue().Replace(text, m => m.Groups[1].Value + "=[REDACTED]");
    }
}

/// <summary>Bounded in-memory log (ring buffer) with secret redaction on write.</summary>
public sealed class DiagnosticsLog
{
    readonly ConcurrentQueue<string> _lines = new();
    readonly int _max;
    public event Action<string>? LineAdded;
    public DiagnosticsLog(int maxLines = 2000) { _max = maxLines; }

    public void Info(string m) => Add("INFO", m);
    public void Warn(string m) => Add("WARN", m);
    public void Error(string m) => Add("ERROR", m);

    void Add(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {Redactor.Redact(message)}";
        _lines.Enqueue(line);
        while (_lines.Count > _max && _lines.TryDequeue(out _)) { }
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<string> Snapshot() => _lines.ToArray();

    public void Export(string path) => File.WriteAllLines(path, _lines.Select(Redactor.Redact));
}
