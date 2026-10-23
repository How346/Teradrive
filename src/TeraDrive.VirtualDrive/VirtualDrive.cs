using TeraDrive.Core;

namespace TeraDrive.VirtualDrive;

public enum DriveState { RuntimeMissing, NotImplemented, Unmounted, Mounted }

public sealed record DriveStatus(DriveState State, string Message);

public static class DriveLetterValidator
{
    public static bool IsValid(string? letter) =>
        letter is { Length: 1 } && char.IsAsciiLetter(letter[0]) && !"ABC".Contains(char.ToUpperInvariant(letter[0]));

    public static bool IsInUse(string letter) =>
        OperatingSystem.IsWindows() && DriveInfo.GetDrives().Any(d => d.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Detects whether the WinFsp runtime (the officially maintained Windows user-mode filesystem framework) is installed.</summary>
public static class WinFspDetector
{
    public static bool IsInstalled(out string? installDir)
    {
        installDir = null;
        if (!OperatingSystem.IsWindows()) return false;
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var dir = Path.Combine(pf86, "WinFsp");
        if (File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll"))) { installDir = dir; return true; }
        return false;
    }
}

public interface IVirtualDriveService
{
    DriveStatus GetStatus();
    Task MountAsync(string driveLetter, CancellationToken ct);
    Task UnmountAsync(CancellationToken ct);
}

/// <summary>
/// HONEST STATUS: the WinFsp filesystem adapter (mapping ICloudStorageProvider to a mounted drive) is NOT implemented in this
/// version, and nothing here has been run on Windows. This service only reports whether the WinFsp runtime is present and
/// refuses to mount; it never claims a drive is mounted. See docs/ARCHITECTURE.md for the planned design.
/// </summary>
public sealed class UnavailableVirtualDriveService : IVirtualDriveService
{
    public DriveStatus GetStatus() => WinFspDetector.IsInstalled(out _)
        ? new DriveStatus(DriveState.NotImplemented, "WinFsp runtime detected, but the TeraDrive filesystem adapter is not implemented yet.")
        : new DriveStatus(DriveState.RuntimeMissing, "WinFsp is not installed (install from https://winfsp.dev) and the filesystem adapter is not implemented yet.");

    public Task MountAsync(string driveLetter, CancellationToken ct) =>
        throw new UnsupportedOperationException("Mount", "virtual-drive", GetStatus().Message);

    public Task UnmountAsync(CancellationToken ct) => Task.CompletedTask;
}
