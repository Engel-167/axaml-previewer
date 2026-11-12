using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AxamlPreview.Services;

/// <summary>
/// The app's colour roles (Material 3 names, see Theme/Colors.axaml). Every theme, built in or read from Zed, fills all of
/// them, so the views only ever use these keys through DynamicResource.
/// </summary>
public sealed record Palette(
    bool IsDark,
    Color Background,
    Color SurfaceContainer,
    Color SurfaceContainerHigh,
    Color SurfaceContainerLowest,
    Color PrimaryContainer,
    Color OnPrimaryContainer,
    Color Primary,
    Color OnPrimary,
    Color OnSurface,
    Color OnSurfaceVariant,
    Color Error,
    Color ErrorContainer,
    Color StateLayer,
    Color PrimaryContainerHover,
    Color PrimaryContainerPressed,
    Color PrimaryHover,
    Color PrimaryPressed,
    Color SurfaceContainerHighHover,
    Color GridLine,
    Color ScrollTrack,
    Color ScrollHandle)
{
    /// <summary>The resource key → colour pairs, in the names Colors.axaml uses.</summary>
    public IEnumerable<(string Key, Color Color)> Entries()
    {
        yield return (nameof(Background), Background);
        yield return (nameof(SurfaceContainer), SurfaceContainer);
        yield return (nameof(SurfaceContainerHigh), SurfaceContainerHigh);
        yield return (nameof(SurfaceContainerLowest), SurfaceContainerLowest);
        yield return (nameof(PrimaryContainer), PrimaryContainer);
        yield return (nameof(OnPrimaryContainer), OnPrimaryContainer);
        yield return (nameof(Primary), Primary);
        yield return (nameof(OnPrimary), OnPrimary);
        yield return (nameof(OnSurface), OnSurface);
        yield return (nameof(OnSurfaceVariant), OnSurfaceVariant);
        yield return (nameof(Error), Error);
        yield return (nameof(ErrorContainer), ErrorContainer);
        yield return (nameof(StateLayer), StateLayer);
        yield return (nameof(PrimaryContainerHover), PrimaryContainerHover);
        yield return (nameof(PrimaryContainerPressed), PrimaryContainerPressed);
        yield return (nameof(PrimaryHover), PrimaryHover);
        yield return (nameof(PrimaryPressed), PrimaryPressed);
        yield return (nameof(SurfaceContainerHighHover), SurfaceContainerHighHover);
        yield return (nameof(GridLine), GridLine);
        yield return (nameof(ScrollTrack), ScrollTrack);
        yield return (nameof(ScrollHandle), ScrollHandle);
    }

    /// <summary>
    /// A full palette from the few colours every editor theme has: background, two surface steps, text, muted text, an
    /// accent and an error colour. The containers, state layers and canvas colours are mixed from those, the way M3 does.
    /// </summary>
    public static Palette From(bool dark, Color background, Color surface, Color surfaceHigh, Color lowest, Color text, Color muted, Color accent, Color error)
    {
        var primaryContainer = dark ? Mix(surface, accent, 0.35) : Mix(background, accent, 0.2);
        var onPrimaryContainer = dark ? Mix(accent, Colors.White, 0.55) : Mix(accent, Colors.Black, 0.45);
        var onPrimary = Luminance(accent) > 0.4 ? Mix(accent, Colors.Black, 0.8) : Colors.White;
        return new Palette(
            dark, background, surface, surfaceHigh, lowest,
            primaryContainer, onPrimaryContainer, accent, onPrimary, text, muted,
            error, Mix(surface, error, dark ? 0.35 : 0.2),
            WithAlpha(text, 0x14),
            Mix(primaryContainer, onPrimaryContainer, 0.08), Mix(primaryContainer, onPrimaryContainer, 0.12),
            Mix(accent, onPrimary, 0.08), Mix(accent, onPrimary, 0.12),
            Mix(surfaceHigh, text, 0.08),
            WithAlpha(accent, dark ? (byte)0x1A : (byte)0x1F),
            WithAlpha(surfaceHigh, 0x50),
            accent);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static double Luminance(Color c)
    {
        static double Channel(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}

/// <summary>A theme the app can wear. <see cref="Source"/> says where it came from ("Built-in", or the Zed extension).</summary>
public sealed record AppTheme(string Id, string Name, string Source, Palette Palette);

/// <summary>The themes shipped with the app: Material (the Figma mockups' palette) plus popular editor palettes.</summary>
public static class BuiltInThemes
{
    private static Color C(string hex) => Color.Parse(hex);

    private static AppTheme Make(string name, bool dark, string bg, string surface, string high, string lowest, string text, string muted, string accent, string error) =>
        new("builtin:" + name, name, "Built-in",
            Palette.From(dark, C(bg), C(surface), C(high), C(lowest), C(text), C(muted), C(accent), C(error)));

    /// <summary>Material 3, exactly as Theme/Colors.axaml has it (the colours the mockups were drawn with).</summary>
    public static readonly AppTheme MaterialDark = new("builtin:Material Dark", "Material Dark", "Built-in", new Palette(
        true, C("#141218"), C("#211F26"), C("#2B2930"), C("#0F0D13"), C("#4F378B"), C("#EADDFF"), C("#D0BCFE"), C("#381E72"),
        C("#E6E0E9"), C("#CAC4D0"), C("#F2B8B5"), C("#8C1D18"), C("#14EADDFF"), C("#5D4599"), C("#6950A4"), C("#DCCBFE"),
        C("#E6D9FF"), C("#36343B"), C("#1AA303F4"), C("#2936343B"), C("#D0BCFE")));

    public static readonly AppTheme MaterialLight = new("builtin:Material Light", "Material Light", "Built-in", new Palette(
        false, C("#FEF7FF"), C("#F3EDF7"), C("#ECE6F0"), C("#FFFFFF"), C("#EADDFF"), C("#4F378A"), C("#65558F"), C("#FFFFFF"),
        C("#1D1B20"), C("#49454F"), C("#B3261E"), C("#F9DEDC"), C("#141D1B20"), C("#DED0F6"), C("#D7C9F1"), C("#716398"),
        C("#77699C"), C("#DBD6DF"), C("#1F6750A4"), C("#33CAC4D0"), C("#65558F")));

    public static IReadOnlyList<AppTheme> All { get; } =
    [
        MaterialDark,
        MaterialLight,
        //    name                    dark   background surface    high       lowest     text       muted      accent     error
        Make("Catppuccin Mocha",      true,  "#1E1E2E", "#313244", "#45475A", "#11111B", "#CDD6F4", "#A6ADC8", "#CBA6F7", "#F38BA8"),
        Make("Catppuccin Macchiato",  true,  "#24273A", "#363A4F", "#494D64", "#181926", "#CAD3F5", "#A5ADCB", "#C6A0F6", "#ED8796"),
        Make("Catppuccin Frappé",     true,  "#303446", "#414559", "#51576D", "#232634", "#C6D0F5", "#A5ADCE", "#CA9EE6", "#E78284"),
        Make("Catppuccin Latte",      false, "#EFF1F5", "#E6E9EF", "#DCE0E8", "#FFFFFF", "#4C4F69", "#6C6F85", "#8839EF", "#D20F39"),
        Make("One Dark",              true,  "#282C33", "#2F343E", "#3B414D", "#1E2227", "#DCE0E5", "#A9AFBC", "#74ADE8", "#D07277"),
        Make("One Light",             false, "#FAFAFA", "#EBEBEC", "#DCDCDD", "#FFFFFF", "#242529", "#58585A", "#5C78E2", "#D36151"),
        Make("Ayu Dark",              true,  "#0D1017", "#131721", "#1F2430", "#0A0D13", "#BFBDB6", "#8A8986", "#5AC1FE", "#EF7177"),
        Make("Ayu Mirage",            true,  "#242936", "#2A303E", "#353C4D", "#1F2430", "#CCCAC2", "#9A9A94", "#73CFFF", "#FF6666"),
        Make("Ayu Light",             false, "#FCFCFC", "#ECECED", "#DFDFE0", "#FFFFFF", "#5C6166", "#8A9199", "#3B9EE5", "#E65050"),
        Make("Gruvbox Dark",          true,  "#282828", "#32302F", "#3C3836", "#1D2021", "#EBDBB2", "#A89984", "#FABD2F", "#FB4934"),
        Make("Gruvbox Light",         false, "#FBF1C7", "#F2E5BC", "#EBDBB2", "#F9F5D7", "#3C3836", "#7C6F64", "#B57614", "#9D0006"),
        Make("Dracula",               true,  "#282A36", "#343746", "#44475A", "#191A21", "#F8F8F2", "#A4A9C7", "#BD93F9", "#FF5555"),
        Make("Nord",                  true,  "#2E3440", "#3B4252", "#434C5E", "#242933", "#ECEFF4", "#A3ABB9", "#88C0D0", "#BF616A"),
        Make("Tokyo Night",           true,  "#1A1B26", "#24283B", "#292E42", "#16161E", "#C0CAF5", "#A9B1D6", "#7AA2F7", "#F7768E"),
        Make("Rosé Pine",             true,  "#191724", "#1F1D2E", "#26233A", "#12101A", "#E0DEF4", "#908CAA", "#C4A7E7", "#EB6F92"),
        Make("Rosé Pine Dawn",        false, "#FAF4ED", "#F2E9E1", "#E8DED5", "#FFFAF3", "#575279", "#797593", "#907AA9", "#B4637A"),
        Make("Solarized Dark",        true,  "#002B36", "#073642", "#0D4150", "#00212B", "#EEE8D5", "#93A1A1", "#268BD2", "#DC322F"),
        Make("Solarized Light",       false, "#FDF6E3", "#EEE8D5", "#E4DDC8", "#FFFBEF", "#073642", "#586E75", "#268BD2", "#DC322F"),
        Make("Monokai",               true,  "#272822", "#2F302A", "#3E3D32", "#1E1F1C", "#F8F8F2", "#A59F85", "#A6E22E", "#F92672"),
        Make("Everforest Dark",       true,  "#2D353B", "#343F44", "#3D484D", "#232A2E", "#D3C6AA", "#9DA9A0", "#A7C080", "#E67E80"),
    ];
}

/// <summary>
/// Themes installed in Zed: ~/.config/zed/themes/*.json and every extension's themes/*.json. A Zed theme file is a family
/// ({"name", "themes": [{"name", "appearance", "style": {…}}]}) whose colours are CSS hex (#RRGGBB or #RRGGBBAA).
/// </summary>
public static class ZedThemes
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string ConfigDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zed");
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
            return Path.Combine(config, "zed");
        }
    }

    private static string DataDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "Zed");
            if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zed");
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
            return Path.Combine(data, "zed");
        }
    }

    public static List<AppTheme> Load()
    {
        var files = new List<(string Path, string Source)>();
        void Add(string dir, string source)
        {
            try
            {
                if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(f => (f, source)));
            }
            catch (Exception) { }
        }
        Add(Path.Combine(ConfigDirectory, "themes"), "Zed");
        try
        {
            var installed = Path.Combine(DataDirectory, "extensions", "installed");
            if (Directory.Exists(installed))
            {
                foreach (var ext in Directory.EnumerateDirectories(installed).Order(StringComparer.Ordinal))
                    Add(Path.Combine(ext, "themes"), "Zed · " + Path.GetFileName(ext));
            }
        }
        catch (Exception) { }

        var result = new List<AppTheme>();
        foreach (var (path, source) in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
                if (!doc.RootElement.TryGetProperty("themes", out var themes) || themes.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in themes.EnumerateArray())
                {
                    if (FromZed(t, source) is { } theme) result.Add(theme);
                }
            }
            catch (Exception) { } // a theme we cannot read is simply not offered
        }
        return result;
    }

    private static AppTheme? FromZed(JsonElement theme, string source)
    {
        if (!theme.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { Length: > 0 } name) return null;
        if (!theme.TryGetProperty("style", out var style) || style.ValueKind != JsonValueKind.Object) return null;
        var dark = !(theme.TryGetProperty("appearance", out var a) && a.GetString() == "light");

        var backdrop = dark ? Colors.Black : Colors.White;
        Color? Get(Color under, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (style.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && ParseCss(v.GetString()!) is { } c)
                    return Over(c, under);
            }
            return null;
        }

        if (Get(backdrop, "editor.background", "background") is not { } bg) return null;
        if (Get(bg, "text") is not { } text) return null;
        var surface = Get(bg, "elevated_surface.background", "surface.background", "panel.background") ?? bg;
        if (surface == bg) surface = Palette.Mix(bg, text, dark ? 0.06 : 0.04);
        var high = Get(surface, "element.hover", "element.background") ?? surface;
        if (high == surface || high == bg) high = Palette.Mix(surface, text, dark ? 0.08 : 0.06);
        var lowest = dark ? Palette.Mix(bg, Colors.Black, 0.3) : Palette.Mix(bg, Colors.White, 0.6);
        var muted = Get(bg, "text.muted", "text.placeholder") ?? Palette.Mix(text, bg, 0.3);
        var accent = Get(bg, "text.accent", "icon.accent", "border.focused") ?? PlayerCursor(style, bg) ?? (dark ? Color.Parse("#D0BCFE") : Color.Parse("#65558F"));
        var error = Get(bg, "error") ?? (dark ? Color.Parse("#F2B8B5") : Color.Parse("#B3261E"));
        return new AppTheme("zed:" + name, name, source, Palette.From(dark, bg, surface, high, lowest, text, muted, accent, error));
    }

    private static Color? PlayerCursor(JsonElement style, Color bg)
    {
        if (!style.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array || players.GetArrayLength() == 0) return null;
        return players[0].TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String && ParseCss(c.GetString()!) is { } color ? Over(color, bg) : null;
    }

    /// <summary>#RGB, #RRGGBB or #RRGGBBAA (CSS order, unlike Avalonia's #AARRGGBB).</summary>
    public static Color? ParseCss(string s)
    {
        if (s.Length < 4 || s[0] != '#') return null;
        var hex = s[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return hex.Length == 6
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }

    /// <summary>A translucent colour as it looks over <paramref name="under"/>.</summary>
    private static Color Over(Color c, Color under)
    {
        if (c.A == 255) return c;
        var t = c.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(under.R + (c.R - under.R) * t),
            (byte)Math.Round(under.G + (c.G - under.G) * t),
            (byte)Math.Round(under.B + (c.B - under.B) * t));
    }

    /// <summary>
    /// The theme Zed is using, from its settings.json: "theme": "Name", or {"mode": "system"|"light"|"dark", "light": …, "dark": …}.
    /// Null when Zed has no settings or names no theme (Zed's own default is then One Dark / One Light).
    /// </summary>
    public static string? ActiveThemeName(bool systemIsDark)
    {
        try
        {
            var path = Path.Combine(ConfigDirectory, "settings.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
            if (!doc.RootElement.TryGetProperty("theme", out var theme)) return systemIsDark ? "One Dark" : "One Light";
            if (theme.ValueKind == JsonValueKind.String) return theme.GetString();
            if (theme.ValueKind != JsonValueKind.Object) return null;
            var mode = theme.TryGetProperty("mode", out var m) ? m.GetString() : "system";
            var dark = mode switch { "dark" => true, "light" => false, _ => systemIsDark };
            return theme.TryGetProperty(dark ? "dark" : "light", out var n) ? n.GetString() : dark ? "One Dark" : "One Light";
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Puts a theme on the app. "System" leaves Colors.axaml's Material light/dark dictionaries in charge (following the OS);
/// any other theme writes its colours over them in Application.Resources and pins the Fluent theme variant to match, so
/// Fluent's own controls (text boxes, scroll bars, menus) agree.
/// </summary>
public sealed class ThemeManager
{
    public const string SystemId = "system";
    public const string MatchZedId = "zed";

    private readonly Application _app;
    private readonly List<AppTheme> _themes = [];
    private FileSystemWatcher? _zedSettingsWatcher;
    private string _selected = SystemId;

    public ThemeManager(Application app)
    {
        _app = app;
        Reload();
        if (app.PlatformSettings is { } platform) platform.ColorValuesChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }

    public IReadOnlyList<AppTheme> Themes => _themes;

    /// <summary>The theme "Match Zed" currently resolves to (null when Zed's theme is unknown here).</summary>
    public AppTheme? ZedTheme { get; private set; }

    public event Action? Applied;

    public void Reload()
    {
        _themes.Clear();
        _themes.AddRange(BuiltInThemes.All);
        foreach (var t in ZedThemes.Load())
        {
            if (_themes.All(x => x.Name != t.Name)) _themes.Add(t);
        }
    }

    public string Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            WatchZedSettings(value == MatchZedId);
            Apply();
        }
    }

    private bool SystemIsDark => _app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    public void Apply()
    {
        AppTheme? theme = null;
        ZedTheme = null;
        if (_selected == MatchZedId)
        {
            var name = ZedThemes.ActiveThemeName(SystemIsDark);
            theme = ZedTheme = FindByName(name);
        }
        else if (_selected != SystemId)
        {
            theme = _themes.FirstOrDefault(t => t.Id == _selected);
        }

        var resources = _app.Resources;
        if (theme == null)
        {
            foreach (var (key, _) in BuiltInThemes.MaterialDark.Palette.Entries()) resources.Remove(key);
            _app.RequestedThemeVariant = ThemeVariant.Default;
        }
        else
        {
            foreach (var (key, color) in theme.Palette.Entries()) resources[key] = new SolidColorBrush(color);
            _app.RequestedThemeVariant = theme.Palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        Applied?.Invoke();
    }

    /// <summary>Zed's theme by name; a variant Zed has but the app doesn't ("Gruvbox Dark Hard") falls back to its family.</summary>
    public AppTheme? FindByName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var exact = _themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return _themes.Where(t => name.StartsWith(t.Name + " ", StringComparison.OrdinalIgnoreCase)).MaxBy(t => t.Name.Length);
    }

    private void WatchZedSettings(bool on)
    {
        _zedSettingsWatcher?.Dispose();
        _zedSettingsWatcher = null;
        if (!on || !Directory.Exists(ZedThemes.ConfigDirectory)) return;
        try
        {
            // Zed saves settings by writing a new file and renaming it, so watch the folder for any change to settings.json.
            var watcher = new FileSystemWatcher(ZedThemes.ConfigDirectory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
            watcher.EnableRaisingEvents = true;
            _zedSettingsWatcher = watcher;
        }
        catch (Exception) { }
    }
}
