using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using Avalonia.Threading;
using AxamlPreview.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxamlPreview.ViewModels;

public sealed record FileEntry(string Path, string Name, string Folder);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ZedFollower _editor = new();
    private ProjectWorkspace? _workspace;
    private HostLaunch? _launch;
    private PreviewSession? _session;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<string> _allFiles = [];
    private string? _editorFile;
    private DispatcherTimer? _resendTimer;
    private Timer? _liveTimer;
    private int _livePolling;
    /// <summary>What the host is showing: the text last sent and whether it came from Zed's unsaved buffer.</summary>
    private string? _lastSentText;
    private bool _lastSentUnsaved;

    [ObservableProperty] private bool _isHome = true;
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private string _fileLabel = "Pick a view";
    [ObservableProperty] private PreviewTheme _theme;
    [ObservableProperty] private double _zoom = 0.9;
    [ObservableProperty] private bool _isFollowing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _centerMessage = "";
    [ObservableProperty] private bool _hasFrame;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _homeMessage = "";
    [ObservableProperty] private string _fileFilter = "";

    public ObservableCollection<FileEntry> Files { get; } = [];

    /// <summary>Raised on the UI thread for each frame from the host.</summary>
    public event Action<FrameMessage>? Frame;
    public event Action? ResetView;
    public event Action? FitRequested;
    public event Action<PreviewSession?>? SessionChanged;

    public MainViewModel(ThemeManager? themes = null)
    {
        _theme = _settings.Theme;
        _editor.ActiveFileChanged += path => Dispatcher.UIThread.Post(() => OnEditorFile(path));
        Settings = new SettingsViewModel(_settings, themes, _editor.IsAvailable);
        Settings.LiveTypingChanged += _ => UpdateLiveTyping();
    }

    public SettingsViewModel Settings { get; }

    public string ZoomLabel => Math.Round(Zoom * 100).ToString(CultureInfo.InvariantCulture) + "%";
    public string ConnectLabel => IsFollowing ? $"Following {_editor.EditorName}" : "Connect to IDE";
    public string FollowTip => IsFollowing ? $"Following {_editor.EditorName}: the preview shows the .axaml file you are on. Click to stop." : $"Follow {_editor.EditorName}: show the .axaml file you are editing";
    public bool ShowCenterMessage => !string.IsNullOrEmpty(CenterMessage) && (!HasFrame || IsBusy);

    public bool IsNoTheme { get => Theme == PreviewTheme.None; set { if (value) Theme = PreviewTheme.None; } }
    public bool IsLightTheme { get => Theme == PreviewTheme.Light; set { if (value) Theme = PreviewTheme.Light; } }
    public bool IsDarkTheme { get => Theme == PreviewTheme.Dark; set { if (value) Theme = PreviewTheme.Dark; } }

    public void Start(string[] args)
    {
        IsFollowing = _settings.FollowEditor;
        if (args.Length > 0 && File.Exists(args[0]))
        {
            var path = Path.GetFullPath(args[0]);
            if (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            {
                if (ProjectWorkspace.RootFor(path) is { } root) _ = OpenAsync(root, path);
            }
            else if (ProjectWorkspace.IsProjectFile(path))
            {
                _ = OpenAsync(path);
            }
        }
    }

    // ---- opening a project ------------------------------------------------------------------------------------

    public async Task OpenAsync(string projectPath, string? file = null)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        StopSession();
        HomeMessage = "";
        Error = null;
        Notice = null;

        ProjectWorkspace workspace;
        try
        {
            workspace = await Task.Run(() => ProjectWorkspace.Load(projectPath), cts.Token);
        }
        catch (Exception e)
        {
            HomeMessage = "Could not read the project: " + e.Message;
            return;
        }
        if (workspace.Startup == null)
        {
            HomeMessage = $"{Path.GetFileName(projectPath)} has no desktop app project (one that references Avalonia.Desktop) to run the preview with.";
            IsHome = true;
            return;
        }

        _workspace = workspace;
        _editor.PreferredRoot = workspace.RootDirectory;
        ProjectName = workspace.DisplayName;
        _settings.LastProject = workspace.RootFile;
        _settings.Save();
        IsHome = false;
        HasFrame = false;
        CurrentFile = null;
        FileLabel = "Pick a view";
        RefreshFiles();
        Watch(workspace.RootDirectory);

        try
        {
            IsBusy = true;
            CenterMessage = $"Reading {workspace.Startup.Name}…";
            _launch = await DotnetBuild.LaunchInfoAsync(workspace.Startup, cts.Token);
            if (!File.Exists(_launch.AppDllPath) && !await BuildAsync(cts.Token)) return;
            if (cts.IsCancellationRequested) return;
            StartSession();
            CheckStale();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            IsBusy = false;
            CenterMessage = "";
            Error = e.Message;
            return;
        }
        finally
        {
            if (_loadCts == cts) IsBusy = false;
        }

        var first = file
            ?? (_editorFile != null && workspace.Contains(_editorFile) && _editorFile.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ? _editorFile : null)
            ?? _allFiles.FirstOrDefault(f => Path.GetFileName(f) is "MainView.axaml")
            ?? _allFiles.FirstOrDefault(f => Path.GetFileName(f) is "MainWindow.axaml");
        if (first != null) Select(first);
        else CenterMessage = "Pick a view to preview";
    }

    private async Task<bool> BuildAsync(CancellationToken ct)
    {
        if (_workspace?.Startup is not { } startup) return false;
        StopSession();
        IsBusy = true;
        Error = null;
        Notice = null;
        CenterMessage = $"Building {startup.Name}…";
        try
        {
            var result = await DotnetBuild.BuildAsync(startup, ct);
            if (!result.Succeeded)
            {
                Error = "Build failed:\n" + string.Join('\n', result.Errors.Take(4));
                CenterMessage = HasFrame ? "" : "The build failed.";
                return false;
            }
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartSession()
    {
        if (_launch == null) return;
        var session = new PreviewSession(_launch);
        session.FrameReceived += frame => Dispatcher.UIThread.Post(() =>
        {
            if (_session != session) return;
            HasFrame = true;
            Frame?.Invoke(frame);
        });
        session.XamlResult += result => Dispatcher.UIThread.Post(() => OnXamlResult(session, result));
        session.Status += text => Dispatcher.UIThread.Post(() =>
        {
            if (_session != session || IsBusy) return;
            if (text.Length == 0) { if (CurrentFile != null && !HasFrame) CenterMessage = "Rendering…"; }
            else CenterMessage = text;
        });
        _session = session;
        SessionChanged?.Invoke(session);
        session.Start();
        _lastSentText = null;
        SendCurrent();
        UpdateLiveTyping();
    }

    private void StopSession()
    {
        var session = _session;
        _session = null;
        UpdateLiveTyping();
        SessionChanged?.Invoke(null);
        session?.Dispose();
    }

    private void OnXamlResult(PreviewSession session, UpdateXamlResultMessage result)
    {
        if (_session != session) return;
        if (result.Error == null && result.Exception == null)
        {
            Error = null;
            if (!IsBusy) CenterMessage = "";
            return;
        }
        var where = result.Exception?.LineNumber is { } line ? $" (line {line}{(result.Exception.LinePosition is { } col ? $", col {col}" : "")})" : "";
        var message = result.Exception?.Message ?? result.Error ?? "Unknown error";
        Error = $"{Path.GetFileName(CurrentFile)}{where}: {message}";
        if (!HasFrame) CenterMessage = "This view could not be rendered.";
    }

    // ---- choosing what to show --------------------------------------------------------------------------------

    public void Select(string file)
    {
        if (_workspace == null) return;
        var changed = file != CurrentFile;
        CurrentFile = file;
        FileLabel = Path.GetFileName(file);
        if (changed)
        {
            Error = null;
            ResetView?.Invoke();
            if (!HasFrame && !IsBusy) CenterMessage = "Rendering…";
        }
        SendCurrent();
    }

    [RelayCommand]
    private void SelectFile(FileEntry? entry)
    {
        if (entry != null) Select(entry.Path);
    }

    /// <summary>Sends the current file: Zed's unsaved text when live typing is on and there is some, else the file on disk.</summary>
    private void SendCurrent()
    {
        if (_session == null || _workspace == null || _launch == null || CurrentFile == null) return;
        var unsaved = Settings.LiveTyping ? _editor.ReadUnsaved(CurrentFile) : null;
        string text;
        if (unsaved != null) text = unsaved;
        else
        {
            try { text = File.ReadAllText(CurrentFile); }
            catch (IOException) { return; }
        }
        SendText(text, unsaved != null);
    }

    private void SendText(string text, bool unsaved)
    {
        if (_session == null || _workspace == null || _launch == null || CurrentFile == null) return;
        _lastSentText = text;
        _lastSentUnsaved = unsaved;
        var owner = _workspace.OwnerOf(CurrentFile) ?? _workspace.Startup!;
        var assembly = Path.Combine(_launch.OutputDirectory, owner.AssemblyName + ".dll");
        var projectPath = "/" + Path.GetRelativePath(owner.Directory, CurrentFile).Replace('\\', '/');
        _session.ShowXaml(XamlTheme.Apply(text, Theme), assembly, projectPath);
    }

    // ---- live typing ----------------------------------------------------------------------------------------------

    /// <summary>Polls Zed's unsaved text for the current file while live typing is on and a preview is running.</summary>
    private void UpdateLiveTyping()
    {
        var on = Settings.LiveTyping && _session != null;
        if (on && _liveTimer == null) _liveTimer = new Timer(_ => PollUnsaved(), null, 0, 250);
        else if (!on && _liveTimer != null)
        {
            _liveTimer.Dispose();
            _liveTimer = null;
            if (_lastSentUnsaved) SendCurrent(); // back to the saved file
        }
    }

    private void PollUnsaved()
    {
        if (Interlocked.Exchange(ref _livePolling, 1) == 1) return;
        try
        {
            var file = CurrentFile;
            if (file == null) return;
            var unsaved = _editor.ReadUnsaved(file);
            Dispatcher.UIThread.Post(() =>
            {
                if (file != CurrentFile || _liveTimer == null) return;
                if (unsaved != null && unsaved != _lastSentText) SendText(unsaved, true);
                else if (unsaved == null && _lastSentUnsaved) SendCurrent(); // saved or reverted: show the file
            });
        }
        finally { Volatile.Write(ref _livePolling, 0); }
    }

    private void OnEditorFile(string path)
    {
        _editorFile = path;
        if (!IsFollowing || !path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
        if (_workspace != null && _workspace.Contains(path))
        {
            if (path != CurrentFile) Select(path);
        }
        else if (ProjectWorkspace.RootFor(path) is { } root)
        {
            _ = OpenAsync(root, path);
        }
    }

    // ---- files and changes -----------------------------------------------------------------------------------

    private void RefreshFiles()
    {
        _allFiles = _workspace?.AxamlFiles() ?? [];
        ApplyFilter();
    }

    partial void OnFileFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Files.Clear();
        if (_workspace == null) return;
        var filter = FileFilter.Trim();
        foreach (var f in _allFiles)
        {
            var name = Path.GetFileName(f);
            var folder = Path.GetRelativePath(_workspace.RootDirectory, Path.GetDirectoryName(f)!).Replace('\\', '/');
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !folder.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Files.Add(new FileEntry(f, name, folder == "." ? "" : folder));
        }
    }

    private void Watch(string directory)
    {
        _watcher?.Dispose();
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += (_, e) => OnFileEvent(e.FullPath, false);
            watcher.Created += (_, e) => OnFileEvent(e.FullPath, true);
            watcher.Deleted += (_, e) => OnFileEvent(e.FullPath, true);
            watcher.Renamed += (_, e) => { OnFileEvent(e.OldFullPath, true); OnFileEvent(e.FullPath, true); };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception) { _watcher = null; }
    }

    private void OnFileEvent(string path, bool listChanged)
    {
        if (ProjectWorkspace.IsBuildOutput(path)) return;
        var axaml = path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
        var code = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        if (!axaml && !code) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (axaml && listChanged) RefreshFiles();
            if (path == CurrentFile)
            {
                // Editors write in bursts (truncate, write, rename); send once it settles.
                _resendTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => { _resendTimer!.Stop(); SendCurrent(); });
                _resendTimer.Stop();
                _resendTimer.Start();
            }
            if (code) Notice = "C# code changed. Rebuild with ⟳ to see it.";
        });
    }

    private void CheckStale()
    {
        if (_workspace == null || _launch == null || !File.Exists(_launch.AppDllPath)) return;
        var built = File.GetLastWriteTimeUtc(_launch.AppDllPath);
        var newer = _workspace.Projects.Select(p => p.Directory).Distinct().Any(dir =>
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                    .Any(f => !ProjectWorkspace.IsBuildOutput(f) && File.GetLastWriteTimeUtc(f) > built);
            }
            catch (Exception) { return false; }
        });
        if (newer) Notice = "C# code changed since the last build. Rebuild with ⟳ to see it.";
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task Refresh()
    {
        if (_workspace == null) return;
        var cts = _loadCts ?? new CancellationTokenSource();
        if (await BuildAsync(cts.Token))
        {
            RefreshFiles();
            StartSession();
            if (CurrentFile == null) CenterMessage = "Pick a view to preview";
        }
    }

    [RelayCommand]
    private void SetTheme(PreviewTheme theme) => Theme = theme;

    [RelayCommand]
    private void SetZoom(string value)
    {
        if (value == "fit") FitRequested?.Invoke();
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) Zoom = z;
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        IsFollowing = !IsFollowing;
    }

    [RelayCommand]
    private void ConnectToIde()
    {
        if (!_editor.IsAvailable)
        {
            HomeMessage = "Zed's workspace database was not found, so there is no editor to follow yet.";
            return;
        }
        IsFollowing = true;
        HomeMessage = "Open an .axaml file in Zed and its project opens here.";
        if (_editorFile != null) OnEditorFile(_editorFile);
    }

    [RelayCommand]
    private void CloseProject()
    {
        _loadCts?.Cancel();
        StopSession();
        _watcher?.Dispose();
        _watcher = null;
        _workspace = null;
        _launch = null;
        CurrentFile = null;
        HasFrame = false;
        Error = null;
        Notice = null;
        Files.Clear();
        HomeMessage = "";
        IsHome = true;
    }

    [RelayCommand]
    private void DismissError() => Error = null;

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    // ---- property side effects ----------------------------------------------------------------------------------

    partial void OnThemeChanged(PreviewTheme value)
    {
        _settings.Theme = value;
        _settings.Save();
        OnPropertyChanged(nameof(IsNoTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        SendCurrent();
    }

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomLabel));

    partial void OnIsFollowingChanged(bool value)
    {
        _settings.FollowEditor = value;
        _settings.Save();
        if (value) _editor.Start();
        else _editor.Stop();
        OnPropertyChanged(nameof(ConnectLabel));
        OnPropertyChanged(nameof(FollowTip));
    }

    partial void OnCenterMessageChanged(string value) => OnPropertyChanged(nameof(ShowCenterMessage));
    partial void OnHasFrameChanged(bool value) => OnPropertyChanged(nameof(ShowCenterMessage));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowCenterMessage));

    public void Dispose()
    {
        _loadCts?.Cancel();
        StopSession();
        _watcher?.Dispose();
        _liveTimer?.Dispose();
        _editor.Dispose();
    }
}
