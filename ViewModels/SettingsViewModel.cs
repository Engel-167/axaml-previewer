using System.Collections.ObjectModel;
using Avalonia.Media;
using AxamlPreview.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxamlPreview.ViewModels;

/// <summary>One row of the theme list, with the colours its swatch shows.</summary>
public sealed partial class ThemeOption(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private IBrush _background = Brushes.Transparent;
    [ObservableProperty] private IBrush _surface = Brushes.Transparent;
    [ObservableProperty] private IBrush _accent = Brushes.Transparent;
    [ObservableProperty] private IBrush _text = Brushes.Transparent;
    [ObservableProperty] private IBrush _muted = Brushes.Transparent;

    public void Show(Palette p)
    {
        Background = new SolidColorBrush(p.Background);
        Surface = new SolidColorBrush(p.SurfaceContainerHigh);
        Accent = new SolidColorBrush(p.Primary);
        Text = new SolidColorBrush(p.OnSurface);
        Muted = new SolidColorBrush(p.OnSurfaceVariant);
    }
}

/// <summary>The settings sheet: the app's theme and live typing.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ThemeManager? _themes;
    private readonly List<ThemeOption> _allOptions = [];
    private bool _loading;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _liveTyping;
    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private string _themeFilter = "";

    public ObservableCollection<ThemeOption> Themes { get; } = [];

    /// <summary>Live typing reads Zed's database; without it the switch is off and explains why.</summary>
    public bool LiveTypingAvailable { get; }

    public string LiveTypingNote => LiveTypingAvailable
        ? "Shows your unsaved changes in Zed as you type, without saving the file."
        : "Needs Zed: its workspace database was not found.";

    public event Action<bool>? LiveTypingChanged;

    public SettingsViewModel(AppSettings settings, ThemeManager? themes, bool liveTypingAvailable)
    {
        _settings = settings;
        _themes = themes;
        LiveTypingAvailable = liveTypingAvailable;
        _liveTyping = settings.LiveTyping && liveTypingAvailable;
        if (_themes != null)
        {
            _themes.Applied += UpdateDynamicRows;
            var saved = settings.AppTheme;
            _themes.Selected = saved is ThemeManager.SystemId or ThemeManager.MatchZedId || _themes.Themes.Any(t => t.Id == saved) ? saved : ThemeManager.SystemId;
            BuildOptions();
        }
    }

    private void BuildOptions()
    {
        _loading = true;
        _allOptions.Clear();
        _allOptions.Add(new ThemeOption(ThemeManager.SystemId, "System"));
        _allOptions.Add(new ThemeOption(ThemeManager.MatchZedId, "Match Zed"));
        foreach (var t in _themes!.Themes)
        {
            var option = new ThemeOption(t.Id, t.Name) { Subtitle = t.Source };
            option.Show(t.Palette);
            _allOptions.Add(option);
        }
        UpdateDynamicRows();
        ApplyFilter();
        SelectedTheme = _allOptions.FirstOrDefault(o => o.Id == _themes.Selected) ?? _allOptions[0];
        _loading = false;
    }

    /// <summary>"System" and "Match Zed" show whatever they currently resolve to.</summary>
    private void UpdateDynamicRows()
    {
        if (_themes == null || _allOptions.Count < 2) return;
        var dark = Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        var system = _allOptions[0];
        system.Subtitle = $"Material, {(dark ? "dark" : "light")} like the system";
        system.Show((dark ? BuiltInThemes.MaterialDark : BuiltInThemes.MaterialLight).Palette);

        var zed = _allOptions[1];
        var name = ZedThemes.ActiveThemeName(dark);
        var resolved = _themes.FindByName(name);
        zed.Subtitle = name == null ? "Zed's settings were not found" : resolved != null ? $"Now {name}" : $"{name} is not available here; using System";
        zed.Show((resolved ?? (dark ? BuiltInThemes.MaterialDark : BuiltInThemes.MaterialLight)).Palette);
    }

    partial void OnThemeFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selected = SelectedTheme;
        var filter = ThemeFilter.Trim();
        Themes.Clear();
        foreach (var o in _allOptions)
        {
            if (filter.Length == 0 || o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || o.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Themes.Add(o);
        }
        if (selected != null && Themes.Contains(selected)) SelectedTheme = selected;
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value == null || _themes == null) return;
        if (_themes.Selected != value.Id) _themes.Selected = value.Id;
        if (_loading || _settings.AppTheme == value.Id) return;
        _settings.AppTheme = value.Id;
        _settings.Save();
    }

    partial void OnLiveTypingChanged(bool value)
    {
        if (value && !LiveTypingAvailable)
        {
            LiveTyping = false;
            return;
        }
        _settings.LiveTyping = value;
        _settings.Save();
        LiveTypingChanged?.Invoke(value);
    }

    [RelayCommand]
    private void Open()
    {
        IsOpen = true;
        UpdateDynamicRows();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Reads Zed's themes again (after installing a theme extension).</summary>
    [RelayCommand]
    private void ReloadThemes()
    {
        if (_themes == null) return;
        _themes.Reload();
        BuildOptions();
        _themes.Apply();
    }
}
