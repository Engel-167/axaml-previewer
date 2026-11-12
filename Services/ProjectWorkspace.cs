using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AxamlPreview.Services;

/// <summary>One .csproj as far as the previewer cares: where it is, what it builds and whether it is a desktop app head.</summary>
public sealed record ProjectFile(string Path, string Directory, string AssemblyName, bool IsDesktopApp, IReadOnlyList<string> References)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// A project or solution the user opened: its projects, the desktop head the previewer runs (the one with
/// Program.BuildAvaloniaApp) and the .axaml files that can be previewed.
/// </summary>
public sealed partial class ProjectWorkspace
{
    public required string RootFile { get; init; }
    public required string RootDirectory { get; init; }
    public required IReadOnlyList<ProjectFile> Projects { get; init; }
    public ProjectFile? Startup { get; init; }

    public string DisplayName => System.IO.Path.GetFileName(RootFile);

    public static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    public static ProjectWorkspace Load(string file)
    {
        file = System.IO.Path.GetFullPath(file);
        if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = ReadProject(file);
            // A library (the shared views project) is previewed through its app head: use the solution that holds both.
            if (!project.IsDesktopApp && SolutionAbove(file) is { } solution)
            {
                var ws = Load(solution);
                if (ws.Startup != null && ws.Projects.Any(p => SamePath(p.Path, file))) return ws;
            }
            var all = WithReferences([project]);
            return new ProjectWorkspace
            {
                RootFile = file,
                RootDirectory = System.IO.Path.GetDirectoryName(file)!,
                Projects = all,
                Startup = PickStartup(all),
            };
        }

        var projects = WithReferences(SolutionProjects(file).Where(File.Exists).Select(ReadProject).ToList());
        return new ProjectWorkspace
        {
            RootFile = file,
            RootDirectory = System.IO.Path.GetDirectoryName(file)!,
            Projects = projects,
            Startup = PickStartup(projects),
        };
    }

    /// <summary>The solution or project an .axaml file belongs to: the nearest .slnx/.sln above it, else its .csproj.</summary>
    public static string? RootFor(string axamlFile)
    {
        var project = OwnerProjectFile(axamlFile);
        if (project != null && SolutionAbove(project) is { } solution) return solution;
        return project;
    }

    /// <summary>The project whose directory holds the file (deepest match).</summary>
    public ProjectFile? OwnerOf(string file) =>
        Projects.Where(p => IsUnder(file, p.Directory)).OrderByDescending(p => p.Directory.Length).FirstOrDefault();

    public bool Contains(string file) => OwnerOf(file) != null || IsUnder(file, RootDirectory);

    /// <summary>Every .axaml file in the workspace's projects, outside build output.</summary>
    public IReadOnlyList<string> AxamlFiles()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in Projects.Select(p => p.Directory).Distinct())
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories))
                    if (!IsBuildOutput(f)) files.Add(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return [.. files];
    }

    public static bool IsBuildOutput(string path)
    {
        var sep = System.IO.Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}.git{sep}");
    }

    private static ProjectFile? PickStartup(IReadOnlyList<ProjectFile> projects)
    {
        var apps = projects.Where(p => p.IsDesktopApp).ToList();
        return apps.FirstOrDefault(p => p.Name.EndsWith(".Desktop", StringComparison.OrdinalIgnoreCase)) ?? apps.FirstOrDefault();
    }

    private static List<ProjectFile> WithReferences(List<ProjectFile> projects)
    {
        var seen = projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var queue = new Queue<ProjectFile>(projects);
        while (queue.TryDequeue(out var p))
        {
            foreach (var r in p.References)
            {
                if (!File.Exists(r) || !seen.Add(r)) continue;
                var project = ReadProject(r);
                projects.Add(project);
                queue.Enqueue(project);
            }
        }
        return projects;
    }

    private static ProjectFile ReadProject(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        string? outputType = null, assemblyName = null;
        var desktop = false;
        var references = new List<string>();
        try
        {
            var doc = XDocument.Load(path);
            foreach (var el in doc.Descendants())
            {
                switch (el.Name.LocalName)
                {
                    case "OutputType": outputType ??= el.Value.Trim(); break;
                    case "AssemblyName": assemblyName ??= el.Value.Trim(); break;
                    case "PackageReference" when (string?)el.Attribute("Include") == "Avalonia.Desktop": desktop = true; break;
                    case "ProjectReference" when el.Attribute("Include") is { } include:
                        references.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, include.Value.Replace('\\', '/'))));
                        break;
                }
            }
        }
        catch (Exception) { }

        var exe = outputType is not null && (outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase));
        // Desktop heads reference Avalonia.Desktop; a project that sets it up some other way still starts a classic desktop lifetime.
        if (exe && !desktop) desktop = MentionsDesktopLifetime(dir);
        return new ProjectFile(path, dir, string.IsNullOrEmpty(assemblyName) || assemblyName.Contains('$') ? System.IO.Path.GetFileNameWithoutExtension(path) : assemblyName,
            exe && desktop, references);
    }

    private static bool MentionsDesktopLifetime(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                .Any(f => File.ReadAllText(f).Contains("StartWithClassicDesktopLifetime", StringComparison.Ordinal));
        }
        catch (Exception) { return false; }
    }

    private static IEnumerable<string> SolutionProjects(string solution)
    {
        var dir = System.IO.Path.GetDirectoryName(solution)!;
        var text = File.ReadAllText(solution);
        IEnumerable<string> paths;
        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            paths = XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "Project")
                .Select(e => (string?)e.Attribute("Path")).OfType<string>();
        }
        else
        {
            paths = SlnProject().Matches(text).Select(m => m.Groups[1].Value);
        }
        return paths.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p.Replace('\\', '/'))));
    }

    private static string? OwnerProjectFile(string file)
    {
        for (var dir = System.IO.Path.GetDirectoryName(file); dir != null; dir = System.IO.Path.GetDirectoryName(dir))
        {
            var project = SafeFiles(dir, "*.csproj").FirstOrDefault();
            if (project != null) return project;
        }
        return null;
    }

    private static string? SolutionAbove(string projectFile)
    {
        for (var dir = System.IO.Path.GetDirectoryName(projectFile); dir != null; dir = System.IO.Path.GetDirectoryName(dir))
        {
            var solution = SafeFiles(dir, "*.slnx").Concat(SafeFiles(dir, "*.sln")).FirstOrDefault();
            if (solution != null) return solution;
        }
        return null;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (Exception) { return []; }
    }

    private static bool IsUnder(string file, string dir) =>
        file.StartsWith(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool SamePath(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.Ordinal);

    [GeneratedRegex("^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex SlnProject();
}
