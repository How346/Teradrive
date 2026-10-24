using System.IO;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using TeraDrive.Cache;
using TeraDrive.Cloud;
using TeraDrive.Core;
using TeraDrive.Games;
using TeraDrive.Media;
using TeraDrive.Transfers;
using TeraDrive.VirtualDrive;

namespace TeraDrive.App.ViewModels;

public sealed class FileRow
{
    public FileRow(CloudItem item, string state) { Item = item; State = state; }
    public CloudItem Item { get; }
    public string Name => (Item.IsDirectory ? "📁 " : "📄 ") + Item.Name;
    public string SizeText => Item.IsDirectory ? "" : TransferJob.Format(Item.Size);
    public string ModifiedText => Item.Modified?.ToLocalTime().ToString("g") ?? "";
    public string State { get; }
}

public sealed class VideoRow { public required CloudItem Item { get; init; } public string Name => Item.Name; public string SizeText => TransferJob.Format(Item.Size); }
public sealed class CacheRow { public required CacheEntry Entry { get; init; } public string Name => Entry.Key; public string SizeText => TransferJob.Format(Entry.Size); public string Pinned => Entry.Pinned ? "Offline (pinned)" : "Cached"; public string Last => Entry.LastAccess.ToLocalTime().ToString("g"); }
public sealed class GameRow : ViewModelBase { public required GameEntry Game { get; init; } public string Name => Game.Name; public string Path => Game.InstallPath; string _size = "…"; public string Size { get => _size; set => Set(ref _size, value); } }

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    public static readonly string[] SectionNames = { "Overview", "Files", "Transfers", "Smart Cache", "Videos", "Games", "Settings", "Diagnostics" };

    readonly AppPaths _paths = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TeraBoxProvider _terabox = new();
    readonly IVirtualDriveService _drive = new UnavailableVirtualDriveService();
    CacheLease? _videoLease;
    DateTimeOffset? _lastSync;
    CancellationTokenSource _lifetime = new();

    public DiagnosticsLog Log { get; } = new();
    public AppSettings Settings { get; private set; }
    public ICloudStorageProvider Provider { get; private set; }
    public CacheManager Cache { get; private set; }
    public TransferManager Transfers { get; private set; }
    CachedFileService _cached;
    GameLibrary _games;
    public PlaybackPositionStore Positions { get; }

    public MainViewModel()
    {
        AsyncRelayCommand.ErrorHandler = ex => { Log.Error(ex.ToString()); Status = ErrorTranslator.Describe(ex); };
        Settings = SettingsStore.Load(_paths.Settings);
        var cacheDir = string.IsNullOrWhiteSpace(Settings.CacheDirectory) ? _paths.DefaultCache : Settings.CacheDirectory;
        Cache = new CacheManager(cacheDir, Settings.CacheLimitBytes);
        Provider = new LocalFileSystemProvider(string.IsNullOrWhiteSpace(Settings.LocalProviderRoot) ? _paths.DefaultLocalProvider : Settings.LocalProviderRoot);
        Transfers = new TransferManager(Provider, Settings.UploadConcurrency, Settings.DownloadConcurrency, new RetryPolicy(Settings.MaxRetries));
        _cached = new CachedFileService(Cache);
        _games = new GameLibrary(_paths.Games);
        Positions = new PlaybackPositionStore(_paths.Positions);
        Transfers.JobAdded += j => Application.Current.Dispatcher.InvokeAsync(() => Jobs.Add(j));
        _timer.Tick += (_, _) => Tick();
        LoadSettingsFields();
        BuildCommands();
    }

    // ---------- shell ----------
    int _section; public int SelectedSection { get => _section; set { if (Set(ref _section, value)) OnSectionChanged(); } }
    string _status = "Starting…"; public string Status { get => _status; set => Set(ref _status, value); }
    bool _busy; public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    public IReadOnlyList<string> Sections => SectionNames;

    public async Task InitializeAsync()
    {
        try
        {
            await Provider.ConnectAsync(_lifetime.Token);
            _lastSync = DateTimeOffset.Now;
            Log.Info($"Provider '{Provider.ProviderId}' connected. Capabilities: {Provider.Capabilities.Describe()}");
            await RefreshFilesAsync();
            Status = "Ready.";
        }
        catch (Exception ex) { Log.Error(ex.ToString()); Status = ErrorTranslator.Describe(ex); }
        RefreshGames(); RefreshCache(); Tick(); _timer.Start();
    }

    void OnSectionChanged()
    {
        if (SelectedSection == 3) RefreshCache();
        if (SelectedSection == 4 && AllVideos.Count == 0) _ = RunSafe(ScanVideosAsync);
        if (SelectedSection == 7) RefreshDiagnostics();
    }

    async Task RunSafe(Func<Task> action, string? okStatus = null)
    {
        IsBusy = true;
        try { await action(); if (okStatus is not null) Status = okStatus; }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (Exception ex) { Log.Error(ex.ToString()); Status = ErrorTranslator.Describe(ex); }
        finally { IsBusy = false; }
    }

    // ---------- overview ----------
    string _cloudStatus = "", _providerStatus = "", _cacheSummary = "", _xferSummary = "", _driveStatus = "", _syncText = "";
    public string CloudStatus { get => _cloudStatus; set => Set(ref _cloudStatus, value); }
    public string ProviderStatus { get => _providerStatus; set => Set(ref _providerStatus, value); }
    public string CacheSummary { get => _cacheSummary; set => Set(ref _cacheSummary, value); }
    public string TransferSummary { get => _xferSummary; set => Set(ref _xferSummary, value); }
    public string DriveStatus { get => _driveStatus; set => Set(ref _driveStatus, value); }
    public string SyncText { get => _syncText; set => Set(ref _syncText, value); }
    string _upSpeed = "0 B/s", _downSpeed = "0 B/s"; public string UploadSpeed { get => _upSpeed; set => Set(ref _upSpeed, value); } public string DownloadSpeed { get => _downSpeed; set => Set(ref _downSpeed, value); }

    void Tick()
    {
        CloudStatus = "TeraBox: not connected — no supported personal-drive API is available (see docs/TERABOX-INTEGRATION.md).";
        ProviderStatus = Provider.IsConnected ? $"{Provider.DisplayName}: connected · capabilities: {Provider.Capabilities.Describe()}" : $"{Provider.DisplayName}: disconnected";
        CacheSummary = $"{TransferJob.Format(Cache.UsedBytes)} of {TransferJob.Format(Cache.LimitBytes)} used · {Cache.Count} cached file(s)";
        var s = Transfers.Snapshot();
        TransferSummary = $"↑ {s.activeUploads} active · ↓ {s.activeDownloads} active · {s.failed} failed";
        UploadSpeed = TransferJob.Format(s.upBps) + "/s"; DownloadSpeed = TransferJob.Format(s.downBps) + "/s";
        DriveStatus = _drive.GetStatus().Message;
        SyncText = _lastSync is null ? "Never" : _lastSync.Value.ToLocalTime().ToString("g");
    }

    // ---------- files ----------
    public ObservableCollection<FileRow> Rows { get; } = new();
    List<CloudItem> _items = new();
    string _path = "/"; public string CurrentPath { get => _path; set => Set(ref _path, value); }
    string _search = ""; public string FileSearch { get => _search; set { if (Set(ref _search, value)) ApplyFileFilter(); } }

    string StateOf(CloudItem i)
    {
        if (i.IsDirectory) return "Folder";
        var e = Cache.Peek(CachedFileService.KeyFor(Provider, i.Path));
        return e is null ? "Not cached" : e.Pinned ? "Offline (pinned)" : "Cached";
    }

    void ApplyFileFilter()
    {
        Rows.Clear();
        foreach (var i in _items.Where(i => FileSearch.Length == 0 || i.Name.Contains(FileSearch, StringComparison.CurrentCultureIgnoreCase))) Rows.Add(new FileRow(i, StateOf(i)));
    }

    public async Task RefreshFilesAsync()
    {
        if (!Provider.IsConnected) { Rows.Clear(); Status = "Provider disconnected."; return; }
        _items = (await Provider.ListDirectoryAsync(CurrentPath, _lifetime.Token)).ToList();
        _lastSync = DateTimeOffset.Now;
        ApplyFileFilter();
    }

    static IEnumerable<FileRow> Sel(object? p) => (p as IList)?.Cast<FileRow>().ToList() ?? new List<FileRow>();

    // ---------- commands ----------
    public System.Windows.Input.ICommand RefreshCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand UpCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenRowCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ImportCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand DownloadCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand NewFolderCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand RenameCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand DeleteCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand PinCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand UnpinCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand PauseAllCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ResumeAllCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ClearFinishedCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand PauseJobCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ResumeJobCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand CancelJobCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand RetryJobCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenCacheFolderCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ClearCacheCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand VerifyCacheCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand SetCacheGbCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ToggleThemeCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand SaveSettingsCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ResetSettingsCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand BrowseCacheDirCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand BrowseLocalRootCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand GoSettingsCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ConnectTeraBoxCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand MountCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand ExportDiagCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand PlayVideoCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand RescanVideosCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand SortVideosCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand AddGameCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand LaunchGameCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand RemoveGameCmd { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenGameFolderCmd { get; private set; } = null!;

    void BuildCommands()
    {
        RefreshCmd = new AsyncRelayCommand(_ => RunSafe(RefreshFilesAsync));
        UpCmd = new AsyncRelayCommand(_ => RunSafe(async () => { CurrentPath = CloudPath.Parent(CurrentPath); await RefreshFilesAsync(); }));
        OpenRowCmd = new AsyncRelayCommand(p => RunSafe(async () =>
        {
            var r = Sel(p).FirstOrDefault(); if (r is null || !r.Item.IsDirectory) return;
            CurrentPath = r.Item.Path; await RefreshFilesAsync();
        }));
        ImportCmd = new AsyncRelayCommand(_ => RunSafe(() =>
        {
            foreach (var f in Dialogs.PickFiles("Import files to " + Provider.DisplayName))
                Transfers.EnqueueUpload(f, CloudPath.Combine(CurrentPath, Path.GetFileName(f)));
            Status = "Upload(s) queued. See Transfers."; return Task.CompletedTask;
        }));
        DownloadCmd = new AsyncRelayCommand(p => RunSafe(() =>
        {
            var files = Sel(p).Where(r => !r.Item.IsDirectory).ToList();
            if (files.Count == 0) { Status = "Select one or more files to download."; return Task.CompletedTask; }
            var dir = Dialogs.PickFolder("Download to…"); if (dir is null) return Task.CompletedTask;
            foreach (var r in files) Transfers.EnqueueDownload(r.Item.Path, Path.Combine(dir, r.Item.Name));
            Status = $"{files.Count} download(s) queued. Existing files will not be overwritten."; return Task.CompletedTask;
        }));
        NewFolderCmd = new AsyncRelayCommand(_ => RunSafe(async () =>
        {
            var n = Dialogs.Prompt("New folder", "Folder name:", "New folder"); if (n is null) return;
            await Provider.CreateDirectoryAsync(CloudPath.Combine(CurrentPath, n.Trim()), _lifetime.Token); await RefreshFilesAsync();
        }));
        RenameCmd = new AsyncRelayCommand(p => RunSafe(async () =>
        {
            var r = Sel(p).FirstOrDefault(); if (r is null) return;
            var n = Dialogs.Prompt("Rename", "New name:", r.Item.Name); if (n is null || n == r.Item.Name) return;
            await Provider.RenameAsync(r.Item.Path, n.Trim(), _lifetime.Token); await RefreshFilesAsync();
        }));
        DeleteCmd = new AsyncRelayCommand(p => RunSafe(async () =>
        {
            var rows = Sel(p).ToList(); if (rows.Count == 0) return;
            if (!Dialogs.Confirm($"Permanently delete {rows.Count} item(s) from {Provider.DisplayName}?\n\nThis deletes the REAL files in the provider, not just cached copies. This cannot be undone.")) return;
            foreach (var r in rows)
            {
                await Provider.DeleteAsync(r.Item.Path, _lifetime.Token);
                Cache.Remove(CachedFileService.KeyFor(Provider, r.Item.Path));
            }
            await RefreshFilesAsync();
        }, "Deleted."));
        PinCmd = new AsyncRelayCommand(p => RunSafe(async () =>
        {
            foreach (var r in Sel(p).Where(r => !r.Item.IsDirectory))
            {
                using var lease = await _cached.EnsureCachedAsync(Provider, r.Item.Path, _lifetime.Token);
                Cache.Pin(CachedFileService.KeyFor(Provider, r.Item.Path));
            }
            ApplyFileFilter(); RefreshCache();
        }, "Pinned for offline use."));
        UnpinCmd = new AsyncRelayCommand(p => RunSafe(() =>
        {
            foreach (var r in Sel(p).Where(r => !r.Item.IsDirectory)) { try { Cache.Unpin(CachedFileService.KeyFor(Provider, r.Item.Path)); } catch (KeyNotFoundException) { } }
            ApplyFileFilter(); RefreshCache(); return Task.CompletedTask;
        }, "Unpinned."));

        PauseAllCmd = new RelayCommand(_ => Transfers.PauseAll());
        ResumeAllCmd = new RelayCommand(_ => Transfers.ResumeAll());
        ClearFinishedCmd = new RelayCommand(_ => { Transfers.ClearFinished(); var live = Transfers.Jobs.ToHashSet(); foreach (var j in Jobs.Where(j => !live.Contains(j)).ToList()) Jobs.Remove(j); });
        PauseJobCmd = new RelayCommand(p => { if (p is TransferJob j) Transfers.Pause(j); });
        ResumeJobCmd = new RelayCommand(p => { if (p is TransferJob j) Transfers.Resume(j); });
        CancelJobCmd = new RelayCommand(p => { if (p is TransferJob j) Transfers.Cancel(j); });
        RetryJobCmd = new RelayCommand(p => { if (p is TransferJob j) Transfers.Retry(j); });

        OpenCacheFolderCmd = new RelayCommand(_ => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Cache.RootDirectory}\"") { UseShellExecute = true }));
        ClearCacheCmd = new AsyncRelayCommand(_ => RunSafe(() =>
        {
            if (!Dialogs.Confirm("Delete cached copies that are not pinned or in use?\n\nOriginal and cloud files are NOT affected.")) return Task.CompletedTask;
            var n = Cache.ClearUnpinned(); RefreshCache(); ApplyFileFilter(); Status = $"Removed {n} cached file(s)."; return Task.CompletedTask;
        }));
        VerifyCacheCmd = new AsyncRelayCommand(_ => RunSafe(async () =>
        {
            var bad = await Cache.VerifyIntegrityAsync(_lifetime.Token); RefreshCache();
            Status = bad.Count == 0 ? "Cache integrity check passed." : $"Removed {bad.Count} corrupt cache entr{(bad.Count == 1 ? "y" : "ies")}.";
        }));
        SetCacheGbCmd = new RelayCommand(p => { if (int.TryParse(p?.ToString(), out var gb)) { Settings.CacheLimitGb = gb; Cache.SetLimit(Settings.CacheLimitBytes); PersistSettings(); LoadSettingsFields(); Tick(); Status = $"Cache limit set to {gb} GB."; } });

        ToggleThemeCmd = new RelayCommand(_ => { Settings.Theme = Settings.Theme == "Dark" ? "Light" : "Dark"; App.ApplyTheme(Settings.Theme); PersistSettings(); });
        SaveSettingsCmd = new RelayCommand(_ => SaveSettings());
        ResetSettingsCmd = new RelayCommand(_ => { if (Dialogs.Confirm("Reset all settings to defaults?")) { Settings = new AppSettings(); PersistSettings(); LoadSettingsFields(); App.ApplyTheme(Settings.Theme); Status = "Settings reset. Restart to apply cache/provider/concurrency changes."; } });
        BrowseCacheDirCmd = new RelayCommand(_ => { var d = Dialogs.PickFolder("Cache folder"); if (d is not null) CacheDirText = d; });
        BrowseLocalRootCmd = new RelayCommand(_ => { var d = Dialogs.PickFolder("Local test-provider folder"); if (d is not null) LocalRootText = d; });
        GoSettingsCmd = new RelayCommand(_ => SelectedSection = 6);
        ConnectTeraBoxCmd = new AsyncRelayCommand(_ => RunSafe(() => _terabox.ConnectAsync(_lifetime.Token)));
        MountCmd = new AsyncRelayCommand(_ => RunSafe(() => _drive.MountAsync(DriveLetterText, _lifetime.Token)));
        ExportDiagCmd = new RelayCommand(_ =>
        {
            var f = Dialogs.SaveFile("Export diagnostics (secrets redacted)", $"teradrive-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.log"); if (f is null) return;
            Log.Export(f); Status = "Diagnostics exported (secrets redacted).";
        });

        PlayVideoCmd = new AsyncRelayCommand(p => RunSafe(() => PlayAsync(p as VideoRow)));
        RescanVideosCmd = new AsyncRelayCommand(_ => RunSafe(ScanVideosAsync));
        SortVideosCmd = new RelayCommand(p => { VideoSort = p?.ToString() ?? "Name"; ApplyVideoFilter(); });

        AddGameCmd = new AsyncRelayCommand(_ => RunSafe(() =>
        {
            var exe = Dialogs.PickExe(); if (exe is null) return Task.CompletedTask;
            var name = Dialogs.Prompt("Add game", "Display name:", Path.GetFileNameWithoutExtension(exe)); if (name is null) return Task.CompletedTask;
            _games.Add(name, exe); RefreshGames(); return Task.CompletedTask;
        }));
        LaunchGameCmd = new RelayCommand(p => { if (p is GameRow g) { try { GameLauncher.Launch(g.Game); Status = "Launched " + g.Name; } catch (Exception ex) { Status = ErrorTranslator.Describe(ex); Log.Error(ex.ToString()); } } });
        RemoveGameCmd = new RelayCommand(p => { if (p is GameRow g && Dialogs.Confirm($"Remove the shortcut for '{g.Name}'?\n\nGame files on disk are NOT deleted.")) { _games.Remove(g.Game.Id); RefreshGames(); } });
        OpenGameFolderCmd = new RelayCommand(p => { if (p is GameRow g && Directory.Exists(g.Game.InstallPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{g.Game.InstallPath}\"") { UseShellExecute = true }); });
    }

    // ---------- transfers ----------
    public ObservableCollection<TransferJob> Jobs { get; } = new();

    // ---------- cache ----------
    public ObservableCollection<CacheRow> CacheRows { get; } = new();
    void RefreshCache() { CacheRows.Clear(); foreach (var e in Cache.Entries()) CacheRows.Add(new CacheRow { Entry = e }); Tick(); }

    // ---------- videos ----------
    public ObservableCollection<VideoRow> Videos { get; } = new();
    List<CloudItem> AllVideos { get; } = new();
    string _vsearch = ""; public string VideoSearch { get => _vsearch; set { if (Set(ref _vsearch, value)) ApplyVideoFilter(); } }
    public string VideoSort { get; private set; } = "Name";
    string _vstatus = "Select a video. Remote files are cached completely before playback (no partial streaming)."; public string VideoStatus { get => _vstatus; set => Set(ref _vstatus, value); }
    bool _vbusy; public bool VideoBusy { get => _vbusy; set => Set(ref _vbusy, value); }
    public event Action<string, string, double>? PlayRequested;

    async Task ScanVideosAsync()
    {
        AllVideos.Clear();
        var queue = new Queue<(string path, int depth)>(); queue.Enqueue(("/", 0));
        while (queue.Count > 0 && AllVideos.Count < 2000)
        {
            var (path, depth) = queue.Dequeue();
            foreach (var i in await Provider.ListDirectoryAsync(path, _lifetime.Token))
            {
                if (i.IsDirectory) { if (depth < 5) queue.Enqueue((i.Path, depth + 1)); }
                else if (VideoFiles.IsVideo(i.Name)) AllVideos.Add(i);
            }
        }
        ApplyVideoFilter();
    }

    void ApplyVideoFilter()
    {
        IEnumerable<CloudItem> q = AllVideos.Where(i => VideoSearch.Length == 0 || i.Name.Contains(VideoSearch, StringComparison.CurrentCultureIgnoreCase));
        q = VideoSort switch { "Date" => q.OrderByDescending(i => i.Modified), "Size" => q.OrderByDescending(i => i.Size), _ => q.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase) };
        Videos.Clear(); foreach (var i in q) Videos.Add(new VideoRow { Item = i });
    }

    async Task PlayAsync(VideoRow? row)
    {
        if (row is null) return;
        VideoBusy = true; VideoStatus = $"Caching '{row.Name}' before playback…";
        try
        {
            var lease = await _cached.EnsureCachedAsync(Provider, row.Item.Path, _lifetime.Token);
            _videoLease?.Dispose(); _videoLease = lease; // lease protects the file from eviction while playing
            var key = CachedFileService.KeyFor(Provider, row.Item.Path);
            VideoStatus = "Playing: " + row.Name;
            PlayRequested?.Invoke(lease.Path, key, Positions.GetPosition(key));
        }
        finally { VideoBusy = false; }
    }

    public void SavePosition(string key, double seconds) { try { Positions.Save(key, seconds); } catch (IOException ex) { Log.Warn("Could not save playback position: " + ex.Message); } }
    public void ReportVideoError(string message) { VideoStatus = "Playback failed: " + message + " (the codec may not be installed in Windows)."; Log.Warn(VideoStatus); }

    // ---------- games ----------
    public ObservableCollection<GameRow> GameRows { get; } = new();
    void RefreshGames()
    {
        GameRows.Clear();
        foreach (var g in _games.All())
        {
            var row = new GameRow { Game = g }; GameRows.Add(row);
            _ = GameLibrary.ComputeInstallSizeAsync(g, _lifetime.Token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Application.Current.Dispatcher.InvokeAsync(() => { row.Size = TransferJob.Format(t.Result); });
            });
        }
    }

    // ---------- settings ----------
    string _cacheDirText = "", _localRootText = "", _upText = "", _downText = "", _retryText = "", _driveText = "", _gbText = "";
    public string CacheDirText { get => _cacheDirText; set => Set(ref _cacheDirText, value); }
    public string LocalRootText { get => _localRootText; set => Set(ref _localRootText, value); }
    public string UploadConcText { get => _upText; set => Set(ref _upText, value); }
    public string DownloadConcText { get => _downText; set => Set(ref _downText, value); }
    public string RetryText { get => _retryText; set => Set(ref _retryText, value); }
    public string DriveLetterText { get => _driveText; set => Set(ref _driveText, value); }
    public string CacheGbText { get => _gbText; set => Set(ref _gbText, value); }

    void LoadSettingsFields()
    {
        CacheDirText = Settings.CacheDirectory; LocalRootText = Settings.LocalProviderRoot; UploadConcText = Settings.UploadConcurrency.ToString();
        DownloadConcText = Settings.DownloadConcurrency.ToString(); RetryText = Settings.MaxRetries.ToString(); DriveLetterText = Settings.DriveLetter; CacheGbText = Settings.CacheLimitGb.ToString();
    }

    void PersistSettings() { try { SettingsStore.Save(_paths.Settings, Settings); } catch (Exception ex) { Status = ErrorTranslator.Describe(ex); Log.Error(ex.ToString()); } }

    void SaveSettings()
    {
        if (!int.TryParse(UploadConcText, out var up) || !int.TryParse(DownloadConcText, out var down) || !int.TryParse(RetryText, out var rt) || !int.TryParse(CacheGbText, out var gb))
        { Dialogs.Error("Numeric settings must be whole numbers."); return; }
        var candidate = new AppSettings { Theme = Settings.Theme, CacheDirectory = CacheDirText.Trim(), CacheLimitGb = gb, DriveLetter = DriveLetterText.Trim().ToUpperInvariant(),
            UploadConcurrency = up, DownloadConcurrency = down, MaxRetries = rt, LocalProviderRoot = LocalRootText.Trim() };
        var errors = SettingsValidator.Validate(candidate);
        if (errors.Count > 0) { Dialogs.Error(string.Join("\n", errors)); return; }
        Settings = candidate; PersistSettings();
        Cache.SetLimit(Settings.CacheLimitBytes); Tick();
        Status = "Settings saved. Cache folder, provider folder, concurrency and retry changes apply after restart.";
    }

    // ---------- diagnostics ----------
    public ObservableCollection<string> LogLines { get; } = new();
    void RefreshDiagnostics()
    {
        LogLines.Clear(); foreach (var l in Log.Snapshot().TakeLast(500)) LogLines.Add(l);
        LogLines.Add($"-- Provider: {Provider.ProviderId}, capabilities: {Provider.Capabilities.Describe()}");
        LogLines.Add($"-- TeraBox capabilities: {_terabox.Capabilities.Describe()}");
        LogLines.Add($"-- WinFsp installed: {WinFspDetector.IsInstalled(out var dir)} {dir}");
        LogLines.Add($"-- Cache: {Cache.RootDirectory}");
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop(); _lifetime.Cancel();
        _videoLease?.Dispose();
        await Transfers.DisposeAsync();
        Cache.Dispose();
        try { await _drive.UnmountAsync(CancellationToken.None); } catch (Exception) { }
    }
}
