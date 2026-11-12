using System.Text;
using Microsoft.Data.Sqlite;

namespace AxamlPreview.Services;

/// <summary>Reports the file the editor has in front of the user.</summary>
public interface IEditorFollower : IDisposable
{
    string EditorName { get; }
    bool IsAvailable { get; }
    /// <summary>Raised on a background thread with the active file's full path whenever it changes.</summary>
    event Action<string>? ActiveFileChanged;
    void Start();
    void Stop();
}

/// <summary>
/// Follows Zed by reading its workspace database (db/0-*/db.sqlite), which Zed updates within about half a second
/// of switching tabs: the active item of the active pane in the most recently used workspace. The schema is Zed's
/// own and undocumented; anything unexpected just means "no answer".
/// </summary>
public sealed class ZedFollower : IEditorFollower
{
    private const string Query = """
        SELECT CAST(e.path AS TEXT), w.paths
        FROM items i
        JOIN editors e ON e.item_id = i.item_id AND e.workspace_id = i.workspace_id
        JOIN panes p ON p.pane_id = i.pane_id
        JOIN workspaces w ON w.workspace_id = i.workspace_id
        WHERE i.active = 1 AND p.active = 1 AND e.path IS NOT NULL
        ORDER BY w.timestamp DESC
        LIMIT 8
        """;

    private readonly string? _database = FindDatabase();
    private Timer? _timer;
    private string? _last;
    private int _busy;

    public string EditorName => "Zed";
    public bool IsAvailable => _database != null;
    public event Action<string>? ActiveFileChanged;

    /// <summary>When set, prefer the workspace whose folders contain this directory (several Zed windows).</summary>
    public string? PreferredRoot { get; set; }

    public void Start()
    {
        if (_database == null || _timer != null) return;
        _last = null;
        _timer = new Timer(_ => Poll(), null, 0, 400);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var active = ReadActiveFile();
            if (active != null && active != _last)
            {
                _last = active;
                ActiveFileChanged?.Invoke(active);
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private const string UnsavedQuery = """
        SELECT e.contents
        FROM editors e
        JOIN workspaces w ON w.workspace_id = e.workspace_id
        WHERE CAST(e.path AS TEXT) = $path AND e.contents IS NOT NULL
        ORDER BY w.timestamp DESC
        LIMIT 1
        """;

    /// <summary>
    /// The unsaved text Zed holds for <paramref name="path"/>, or null when the buffer is saved (or Zed is not available).
    /// Zed writes a dirty buffer's text to its database about 200 ms after each edit, for restoring unsaved work
    /// ("session": {"restore_unsaved_buffers": true}, the default), and clears it on save.
    /// </summary>
    public string? ReadUnsaved(string path)
    {
        if (_database == null) return null;
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = UnsavedQuery;
            command.Parameters.AddWithValue("$path", path);
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private string? ReadActiveFile()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Query;
        using var reader = command.ExecuteReader();
        string? first = null;
        while (reader.Read())
        {
            var path = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (string.IsNullOrEmpty(path)) continue;
            first ??= path;
            var roots = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (PreferredRoot != null && roots.Split('\n').Any(r => r.Length > 0 && (PreferredRoot.StartsWith(r, StringComparison.Ordinal) || r.StartsWith(PreferredRoot, StringComparison.Ordinal))))
                return path;
        }
        return first;
    }

    /// <summary>The most recently written Zed database among the release channels.</summary>
    private static string? FindDatabase()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dbRoot;
        if (OperatingSystem.IsMacOS()) dbRoot = Path.Combine(home, "Library", "Application Support", "Zed", "db");
        else if (OperatingSystem.IsWindows()) dbRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zed", "db");
        else
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
            dbRoot = Path.Combine(data, "zed", "db");
        }
        if (!Directory.Exists(dbRoot)) return null;
        return Directory.GetDirectories(dbRoot, "0-*")
            .Where(d => !Path.GetFileName(d).Equals("0-global", StringComparison.Ordinal))
            .Select(d => Path.Combine(d, "db.sqlite"))
            .Where(File.Exists)
            .OrderByDescending(f => File.Exists(f + "-wal") ? File.GetLastWriteTimeUtc(f + "-wal") : File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public void Dispose() => Stop();
}
