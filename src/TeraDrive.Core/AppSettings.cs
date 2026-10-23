using System.Text.Json;

namespace TeraDrive.Core;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string CacheDirectory { get; set; } = "";
    public int CacheLimitGb { get; set; } = 10;
    public string DriveLetter { get; set; } = "T";
    public int UploadConcurrency { get; set; } = 2;
    public int DownloadConcurrency { get; set; } = 3;
    public int MaxRetries { get; set; } = 4;
    public string LocalProviderRoot { get; set; } = "";

    public long CacheLimitBytes => (long)CacheLimitGb * 1024L * 1024L * 1024L;
}

public static class SettingsValidator
{
    public static IReadOnlyList<string> Validate(AppSettings s)
    {
        var e = new List<string>();
        if (s.Theme is not ("Dark" or "Light")) e.Add("Theme must be Dark or Light.");
        if (s.CacheLimitGb is < 1 or > 100_000) e.Add("Cache limit must be between 1 and 100000 GB.");
        if (s.UploadConcurrency is < 1 or > 16) e.Add("Upload concurrency must be 1-16.");
        if (s.DownloadConcurrency is < 1 or > 16) e.Add("Download concurrency must be 1-16.");
        if (s.MaxRetries is < 0 or > 10) e.Add("Retries must be 0-10.");
        if (s.DriveLetter is not { Length: 1 } || !char.IsAsciiLetter(s.DriveLetter[0]) || "ABC".Contains(char.ToUpperInvariant(s.DriveLetter[0])))
            e.Add("Drive letter must be a single letter other than A, B or C.");
        if (!string.IsNullOrWhiteSpace(s.CacheDirectory) && !PathGuard.IsValidLocalDirectory(s.CacheDirectory, out var err)) e.Add("Cache directory: " + err);
        if (!string.IsNullOrWhiteSpace(s.LocalProviderRoot) && !PathGuard.IsValidLocalDirectory(s.LocalProviderRoot, out err)) e.Add("Local provider folder: " + err);
        return e;
    }
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Loads settings; a missing or corrupt file yields defaults (a corrupt file is renamed to .corrupt).</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            return SettingsValidator.Validate(s).Count == 0 ? s : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try { File.Move(path, path + ".corrupt", true); } catch (IOException) { }
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings s)
    {
        var errors = SettingsValidator.Validate(s);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, true);
    }
}
