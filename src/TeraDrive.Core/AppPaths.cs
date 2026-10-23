namespace TeraDrive.Core;

public sealed class AppPaths
{
    public string BaseDir { get; }
    public string Settings => Path.Combine(BaseDir, "settings.json");
    public string Games => Path.Combine(BaseDir, "games.json");
    public string Positions => Path.Combine(BaseDir, "playback.json");
    public string DefaultCache => Path.Combine(BaseDir, "Cache");
    public string DefaultLocalProvider => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeraDrive-LocalProvider");

    public AppPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeraDrive");
        Directory.CreateDirectory(BaseDir);
    }
}
