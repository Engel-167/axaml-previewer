using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Input;
using Avalonia.Remote.Protocol.Viewport;

namespace AxamlPreview.Services;

/// <summary>
/// Drives Avalonia's designer host (Avalonia.Designer.HostApp) the way IDEs do: it connects back over tcp-bson,
/// takes XAML, renders it with the app's own assemblies and streams frames; we send input and the render DPI.
/// The host is restarted if it dies; the last XAML is sent again.
/// </summary>
public sealed class PreviewSession : IDisposable
{
    private readonly HostLaunch _launch;
    private readonly IDisposable _server;
    private readonly int _port;
    private readonly object _lock = new();
    private IAvaloniaRemoteTransportConnection? _connection;
    private Process? _host;
    private UpdateXamlMessage? _lastXaml;
    private double _dpi = 96;
    private (double Width, double Height) _viewport = (800, 600);
    private bool _disposed;
    private int _restarts;
    private DateTime _lastStart;

    /// <summary>Raised on the transport thread. Call <see cref="AcknowledgeFrame"/> once the frame has been copied.</summary>
    public event Action<FrameMessage>? FrameReceived;
    public event Action<UpdateXamlResultMessage>? XamlResult;
    /// <summary>Status lines ("Starting previewer…", host errors). Raised on any thread.</summary>
    public event Action<string>? Status;

    public PreviewSession(HostLaunch launch)
    {
        _launch = launch;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server = new BsonTcpTransport().Listen(IPAddress.Loopback, _port, OnConnected);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _lastStart = DateTime.UtcNow;
            Status?.Invoke("Starting previewer…");
            var host = DotnetBuild.StartHost(_launch, $"tcp-bson://127.0.0.1:{_port}/");
            host.EnableRaisingEvents = true;
            host.OutputDataReceived += (_, _) => { };
            host.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } line && !line.StartsWith("   at ", StringComparison.Ordinal))
                    Debug.WriteLine("[host] " + line);
            };
            host.Exited += (_, _) => OnHostExited(host);
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();
            _host = host;
        }
    }

    public void ShowXaml(string xaml, string assemblyPath, string projectPath)
    {
        var message = new UpdateXamlMessage { Xaml = xaml, AssemblyPath = assemblyPath, XamlFileProjectPath = projectPath };
        IAvaloniaRemoteTransportConnection? connection;
        lock (_lock)
        {
            _lastXaml = message;
            connection = _connection;
        }
        connection?.Send(message);
    }

    /// <summary>The DPI to render at: 96 × zoom × screen scaling, so zoomed previews stay sharp.</summary>
    public void SetRenderDpi(double dpi)
    {
        IAvaloniaRemoteTransportConnection? connection;
        lock (_lock)
        {
            if (Math.Abs(dpi - _dpi) < 0.01) return;
            _dpi = dpi;
            connection = _connection;
        }
        connection?.Send(new ClientRenderInfoMessage { DpiX = dpi, DpiY = dpi });
    }

    /// <summary>The room available to a previewed Window (logical units).</summary>
    public void SetViewport(double width, double height)
    {
        IAvaloniaRemoteTransportConnection? connection;
        lock (_lock)
        {
            if (Math.Abs(width - _viewport.Width) < 1 && Math.Abs(height - _viewport.Height) < 1) return;
            _viewport = (width, height);
            connection = _connection;
        }
        connection?.Send(new ClientViewportAllocatedMessage { Width = width, Height = height, DpiX = _dpi, DpiY = _dpi });
    }

    public void AcknowledgeFrame(long sequenceId) => Send(new FrameReceivedMessage { SequenceId = sequenceId });

    public void Send(object message)
    {
        IAvaloniaRemoteTransportConnection? connection;
        lock (_lock) connection = _connection;
        connection?.Send(message);
    }

    private void OnConnected(IAvaloniaRemoteTransportConnection connection)
    {
        connection.OnMessage += OnMessage;
        connection.OnException += (_, e) => Debug.WriteLine("[transport] " + e.Message);
        UpdateXamlMessage? xaml;
        double dpi;
        (double Width, double Height) viewport;
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = connection;
            xaml = _lastXaml;
            dpi = _dpi;
            viewport = _viewport;
        }
        connection.Send(new ClientSupportedPixelFormatsMessage { Formats = [PixelFormat.Bgra8888] });
        connection.Send(new ClientRenderInfoMessage { DpiX = dpi, DpiY = dpi });
        connection.Send(new ClientViewportAllocatedMessage { Width = viewport.Width, Height = viewport.Height, DpiX = dpi, DpiY = dpi });
        if (xaml != null) connection.Send(xaml);
    }

    private void OnMessage(IAvaloniaRemoteTransportConnection connection, object message)
    {
        switch (message)
        {
            case StartDesignerSessionMessage:
                _restarts = 0;
                Status?.Invoke("");
                break;
            case FrameMessage frame:
                FrameReceived?.Invoke(frame);
                break;
            case UpdateXamlResultMessage result:
                XamlResult?.Invoke(result);
                break;
        }
    }

    private void OnHostExited(Process host)
    {
        lock (_lock)
        {
            if (_disposed || host != _host) return;
            _connection?.Dispose();
            _connection = null;
        }
        // Restart a host that died, but give up on one that keeps dying right after start.
        var quick = DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(5);
        _restarts = quick ? _restarts + 1 : 0;
        if (_restarts >= 3)
        {
            Status?.Invoke($"The previewer keeps exiting (code {host.ExitCode}). Rebuild with ⟳ to try again.");
            return;
        }
        Status?.Invoke("Previewer exited; restarting…");
        Task.Delay(500).ContinueWith(_ => Start());
    }

    public void Dispose()
    {
        Process? host;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            host = _host;
            _connection?.Dispose();
            _connection = null;
        }
        try { if (host is { HasExited: false }) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        host?.Dispose();
        _server.Dispose();
    }

    /// <summary>Maps Avalonia key names onto the protocol's enums (they share names).</summary>
    public static KeyEventMessage KeyMessage(bool down, Avalonia.Input.Key key, Avalonia.Input.PhysicalKey physical, string? symbol, Avalonia.Input.KeyModifiers modifiers) => new()
    {
        IsDown = down,
        Key = Enum.TryParse<Key>(key.ToString(), out var k) ? k : Key.None,
        PhysicalKey = Enum.TryParse<PhysicalKey>(physical.ToString(), out var p) ? p : PhysicalKey.None,
        KeySymbol = symbol,
        Modifiers = Modifiers(modifiers),
    };

    public static InputModifiers[] Modifiers(Avalonia.Input.KeyModifiers modifiers, Avalonia.Input.PointerPointProperties? pointer = null)
    {
        var list = new List<InputModifiers>(4);
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) list.Add(InputModifiers.Alt);
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) list.Add(InputModifiers.Control);
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) list.Add(InputModifiers.Shift);
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) list.Add(InputModifiers.Windows);
        if (pointer is { } p)
        {
            if (p.IsLeftButtonPressed) list.Add(InputModifiers.LeftMouseButton);
            if (p.IsRightButtonPressed) list.Add(InputModifiers.RightMouseButton);
            if (p.IsMiddleButtonPressed) list.Add(InputModifiers.MiddleMouseButton);
        }
        return [.. list];
    }
}
