using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AxamlPreview.Services;

/// <summary>What the previewer host needs to start: the host dll matching the project's Avalonia version and the app's outputs.</summary>
public sealed record HostLaunch(string HostAppPath, string AppDllPath, string RuntimeConfigPath, string DepsPath)
{
    public string OutputDirectory => Path.GetDirectoryName(AppDllPath)!;
}

public sealed record BuildResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>Runs the dotnet CLI: MSBuild property queries and builds.</summary>
public static class DotnetBuild
{
    public const string Configuration = "Debug";

    private static readonly Lazy<string> Dotnet = new(FindDotnet);

    public static async Task<HostLaunch> LaunchInfoAsync(ProjectFile startup, CancellationToken ct)
    {
        var props = await PropertiesAsync(startup.Path, ct,
            "AvaloniaPreviewerNetCoreToolPath", "TargetPath", "ProjectRuntimeConfigFilePath", "ProjectDepsFilePath");
        var host = props.GetValueOrDefault("AvaloniaPreviewerNetCoreToolPath");
        if (string.IsNullOrEmpty(host))
            throw new InvalidOperationException($"{startup.Name} does not reference Avalonia (no AvaloniaPreviewerNetCoreToolPath). Restore or build it first.");
        host = Path.GetFullPath(host);
        if (!File.Exists(host)) throw new InvalidOperationException($"Avalonia's designer host is missing: {host}");
        return new HostLaunch(host, props["TargetPath"], props["ProjectRuntimeConfigFilePath"], props["ProjectDepsFilePath"]);
    }

    public static async Task<Dictionary<string, string>> PropertiesAsync(string project, CancellationToken ct, params string[] names)
    {
        var args = new List<string> { "msbuild", project, $"-p:Configuration={Configuration}" };
        args.AddRange(names.Select(n => "-getProperty:" + n));
        var (code, output) = await RunAsync(args, ct);
        if (code != 0) throw new InvalidOperationException("MSBuild could not read the project:\n" + LastLines(output, 8));
        var json = output[output.IndexOf('{')..];
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("Properties", out var properties))
            foreach (var p in properties.EnumerateObject()) result[p.Name] = p.Value.GetString() ?? "";
        return result;
    }

    public static async Task<BuildResult> BuildAsync(ProjectFile project, CancellationToken ct)
    {
        var (code, output) = await RunAsync(["build", project.Path, "-c", Configuration, "-nologo", "-v:q", "-clp:NoSummary"], ct);
        var errors = output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains(": error ", StringComparison.Ordinal)).Distinct().ToList();
        if (code != 0 && errors.Count == 0) errors.Add(LastLines(output, 6));
        return new BuildResult(code == 0, errors);
    }

    /// <summary>Starts the dotnet host with the app's runtime config so the previewer runs on the app's framework and packages.</summary>
    public static Process StartHost(HostLaunch launch, string transport)
    {
        var psi = new ProcessStartInfo(Dotnet.Value)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = launch.OutputDirectory,
        };
        foreach (var a in new[] { "exec", "--runtimeconfig", launch.RuntimeConfigPath, "--depsfile", launch.DepsPath, launch.HostAppPath,
                     "--transport", transport, launch.AppDllPath })
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
    }

    private static async Task<(int Code, string Output)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Dotnet.Value)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
        var output = new StringBuilder();
        var lockObj = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lockObj) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lockObj) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        lock (lockObj) return (process.ExitCode, output.ToString());
    }

    private static string LastLines(string text, int count) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count));

    /// <summary>The dotnet the tool itself runs on when started through it, else the one on PATH or under DOTNET_ROOT.</summary>
    private static string FindDotnet()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath)) return hostPath;
        if (Environment.ProcessPath is { } self && Path.GetFileName(self) == exe) return self;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
        return exe;
    }
}
