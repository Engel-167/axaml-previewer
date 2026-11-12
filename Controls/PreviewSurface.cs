using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using AxamlPreview.Services;
using PointerPressedEventMessage = Avalonia.Remote.Protocol.Input.PointerPressedEventMessage;
using PointerReleasedEventMessage = Avalonia.Remote.Protocol.Input.PointerReleasedEventMessage;
using PointerMovedEventMessage = Avalonia.Remote.Protocol.Input.PointerMovedEventMessage;
using ScrollEventMessage = Avalonia.Remote.Protocol.Input.ScrollEventMessage;
using TextInputEventMessage = Avalonia.Remote.Protocol.Input.TextInputEventMessage;
using Avalonia.Remote.Protocol.Viewport;
using RemoteMouseButton = Avalonia.Remote.Protocol.Input.MouseButton;

namespace AxamlPreview.Controls;

/// <summary>
/// The preview canvas: the frames the designer host renders, on a grid that pans and zooms with them, with the
/// mockup's pill scroll bars. Pointer, wheel and keyboard input over the preview goes to the host, so the design
/// can be used; Ctrl+wheel zooms, dragging the empty canvas (or with the middle button anywhere) pans.
/// </summary>
public sealed class PreviewSurface : Control
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PreviewSurface, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: (_, z) => Math.Clamp(z, MinZoom, MaxZoom));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<PreviewSurface, IBrush?>(nameof(Background));

    public const double MinZoom = 0.1, MaxZoom = 8;
    private const double GridSpacing = 97.5, BarThickness = 10, BarMargin = 10, TopInset = 60, MinHandle = 40;

    // Canvas colours, set from the theme (Colors.axaml) so they follow light/dark.
    public static readonly StyledProperty<IBrush?> GridLineBrushProperty =
        AvaloniaProperty.Register<PreviewSurface, IBrush?>(nameof(GridLineBrush));
    public static readonly StyledProperty<IBrush?> ScrollTrackBrushProperty =
        AvaloniaProperty.Register<PreviewSurface, IBrush?>(nameof(ScrollTrackBrush));
    public static readonly StyledProperty<IBrush?> ScrollHandleBrushProperty =
        AvaloniaProperty.Register<PreviewSurface, IBrush?>(nameof(ScrollHandleBrush));

    static PreviewSurface()
    {
        AffectsRender<PreviewSurface>(BackgroundProperty, GridLineBrushProperty, ScrollTrackBrushProperty, ScrollHandleBrushProperty);
    }

    public IBrush? GridLineBrush { get => GetValue(GridLineBrushProperty); set => SetValue(GridLineBrushProperty, value); }
    public IBrush? ScrollTrackBrush { get => GetValue(ScrollTrackBrushProperty); set => SetValue(ScrollTrackBrushProperty, value); }
    public IBrush? ScrollHandleBrush { get => GetValue(ScrollHandleBrushProperty); set => SetValue(ScrollHandleBrushProperty, value); }

    private WriteableBitmap? _bitmap;
    private Size _content;          // the design's logical size at 100%
    private Point _origin;          // where its top-left corner sits on the canvas
    private bool _centerPending = true;
    private Point? _zoomAnchor;
    private readonly DispatcherTimer _dpiTimer;

    private enum Drag { None, Pan, Forward, HorizontalBar, VerticalBar }
    private Drag _drag;
    private Point _dragStart;
    private Point _originAtDragStart;
    private bool _pointerInside;

    public PreviewSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        _inputClient = new CanvasInputClient(this);
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _inputClient);
        _dpiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dpiTimer.Tick += (_, _) => { _dpiTimer.Stop(); PushRenderDpi(); };
    }

    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    private PreviewSession? _session;
    public PreviewSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            PushRenderDpi();
            PushViewport();
        }
    }

    public bool HasFrame => _bitmap != null;

    private Rect ContentRect => new(_origin, _content * Zoom);

    /// <summary>Copies a frame (UI thread) and tells the host it can send the next one.</summary>
    public void ShowFrame(FrameMessage frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Data is null)
        {
            _session?.AcknowledgeFrame(frame.SequenceId);
            return;
        }
        if (_bitmap == null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var target = _bitmap.Lock())
        {
            var rowBytes = Math.Min(frame.Stride, target.RowBytes);
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(frame.Data, y * frame.Stride, target.Address + y * target.RowBytes, rowBytes);
        }
        _session?.AcknowledgeFrame(frame.SequenceId);

        var dpiX = frame.DpiX > 0 ? frame.DpiX : 96;
        var dpiY = frame.DpiY > 0 ? frame.DpiY : 96;
        var content = new Size(frame.Width * 96 / dpiX, frame.Height * 96 / dpiY);
        if (_centerPending || _content == default)
        {
            _content = content;
            // A newly shown design that does not fit is zoomed out until it does (never in).
            if (FitZoom() is { } fit && fit < Zoom) SetCurrentValue(ZoomProperty, fit);
            Center();
        }
        else if (content != _content)
        {
            // Keep the design's top centre where it was when its size changes.
            var oldCenterX = _origin.X + _content.Width * Zoom / 2;
            _content = content;
            _origin = new Point(oldCenterX - _content.Width * Zoom / 2, _origin.Y);
        }
        InvalidateVisual();
    }

    /// <summary>Re-centre on the next frame (a different file).</summary>
    public void Recenter()
    {
        _centerPending = true;
        if (_content != default) InvalidateVisual();
    }

    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _content = default;
        _centerPending = true;
        InvalidateVisual();
    }

    /// <summary>Zoom so the whole design fits under the bar, and centre it.</summary>
    public void Fit()
    {
        if (FitZoom() is not { } zoom) return;
        _zoomAnchor = null;
        SetCurrentValue(ZoomProperty, zoom);
        Center();
    }

    private double? FitZoom()
    {
        if (_content.Width <= 0 || _content.Height <= 0 || Bounds.Width <= 0) return null;
        var w = Bounds.Width - 2 * (BarMargin + BarThickness + 10);
        var h = Bounds.Height - TopInset - 40 - (BarMargin + BarThickness + 10);
        return Math.Clamp(Math.Floor(Math.Min(w / _content.Width, h / _content.Height) * 100) / 100, MinZoom, MaxZoom);
    }

    private void Center()
    {
        _centerPending = false;
        var size = _content * Zoom;
        var top = TopInset + 40;
        var x = (Bounds.Width - size.Width) / 2;
        // Below the bar and the project name; centred vertically when there is room.
        var y = Math.Max(top, (Bounds.Height - size.Height) / 2);
        _origin = new Point(Math.Round(x), Math.Round(y));
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty)
        {
            var oldZoom = change.GetOldValue<double>();
            var newZoom = change.GetNewValue<double>();
            var anchor = _zoomAnchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
            _zoomAnchor = null;
            if (oldZoom > 0 && _content != default)
                _origin = anchor - (anchor - _origin) * (newZoom / oldZoom);
            _dpiTimer.Stop();
            _dpiTimer.Start();
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_content != default && e.PreviousSize != default)
            _origin += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, 0);
        PushViewport();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PushRenderDpi();
    }

    private void PushRenderDpi()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var dpi = 96 * Zoom * scaling;
        // Keep frames a sane size: a huge design at a high zoom renders at most ~8k pixels across.
        if (_content.Width > 0)
            dpi = Math.Min(dpi, 96 * 8192 / Math.Max(_content.Width, _content.Height));
        _session?.SetRenderDpi(Math.Max(24, dpi));
    }

    private void PushViewport()
    {
        if (Bounds.Width <= 0) return;
        // The room a stretching design gets: the canvas at 100% minus the bars, independent of zoom so zooming scales it.
        _session?.SetViewport(Math.Max(100, Bounds.Width - 80), Math.Max(100, Bounds.Height - TopInset - 40 - 2 * BarMargin - BarThickness - 10));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background != null) context.FillRectangle(Background, bounds);

        // Grid anchored to the design so it moves and scales with it.
        var gridPen = GridLineBrush is { } gridBrush ? new ImmutablePen(gridBrush.ToImmutable(), 1) : null;
        var spacing = GridSpacing * Zoom;
        while (spacing < 24) spacing *= 2;
        var startX = Mod(_origin.X, spacing);
        for (var x = startX; gridPen != null && x < bounds.Width; x += spacing)
            context.DrawLine(gridPen, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, bounds.Height));
        var startY = Mod(_origin.Y, spacing);
        for (var y = startY; gridPen != null && y < bounds.Height; y += spacing)
            context.DrawLine(gridPen, new Point(0, Math.Round(y) + 0.5), new Point(bounds.Width, Math.Round(y) + 0.5));

        if (_bitmap != null)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(_bitmap, new Rect(_bitmap.Size), ContentRect);
        }

        DrawBars(context);
    }

    private static double Mod(double value, double m) => ((value % m) + m) % m;

    // ---- scroll bars ----------------------------------------------------------------------------------------

    private Rect HorizontalTrack => new(BarMargin, Bounds.Height - BarMargin - BarThickness, Math.Max(0, Bounds.Width - 2 * BarMargin - BarThickness - 4), BarThickness);
    private Rect VerticalTrack => new(Bounds.Width - BarMargin - BarThickness, TopInset, BarThickness, Math.Max(0, Bounds.Height - TopInset - 2 * BarMargin - BarThickness - 4));

    /// <summary>The scrollable range along one axis: the design plus half a viewport around it, and the viewport.</summary>
    private (double Min, double Max) Extent(bool horizontal)
    {
        var viewport = horizontal ? Bounds.Width : Bounds.Height;
        var rect = ContentRect;
        if (_content == default) return (0, viewport);
        var lo = horizontal ? rect.Left : rect.Top;
        var hi = horizontal ? rect.Right : rect.Bottom;
        return (Math.Min(0, lo - viewport / 2), Math.Max(viewport, hi + viewport / 2));
    }

    private Rect Handle(bool horizontal)
    {
        var track = horizontal ? HorizontalTrack : VerticalTrack;
        var length = horizontal ? track.Width : track.Height;
        var viewport = horizontal ? Bounds.Width : Bounds.Height;
        var (min, max) = Extent(horizontal);
        var extent = max - min;
        var handle = Math.Clamp(length * viewport / extent, MinHandle, length);
        var position = extent - viewport <= 0 ? (length - handle) / 2 : (0 - min) / (extent - viewport) * (length - handle);
        return horizontal
            ? new Rect(track.X + position, track.Y, handle, BarThickness)
            : new Rect(track.X, track.Y + position, BarThickness, handle);
    }

    private void DrawBars(DrawingContext context)
    {
        if (Bounds.Width < 80 || Bounds.Height < 120) return;
        foreach (var horizontal in new[] { true, false })
        {
            var track = horizontal ? HorizontalTrack : VerticalTrack;
            context.DrawRectangle(ScrollTrackBrush, null, new RoundedRect(track, BarThickness / 2));
            context.DrawRectangle(ScrollHandleBrush, null, new RoundedRect(Handle(horizontal), BarThickness / 2));
        }
    }

    private void ScrollBarDrag(bool horizontal, Point position)
    {
        var track = horizontal ? HorizontalTrack : VerticalTrack;
        var length = horizontal ? track.Width : track.Height;
        var viewport = horizontal ? Bounds.Width : Bounds.Height;
        var (min, max) = Extent(horizontal);
        var handle = horizontal ? Handle(true).Width : Handle(false).Height;
        var travel = Math.Max(1, length - handle);
        var delta = horizontal ? position.X - _dragStart.X : position.Y - _dragStart.Y;
        var moved = delta * (max - min - viewport) / travel;
        _origin = horizontal ? _originAtDragStart.WithX(_originAtDragStart.X - moved) : _originAtDragStart.WithY(_originAtDragStart.Y - moved);
        InvalidateVisual();
    }

    // ---- input ----------------------------------------------------------------------------------------------

    private Point ToDesign(Point p) => new((p.X - _origin.X) / Zoom, (p.Y - _origin.Y) / Zoom);

    private void Forward(object message) => _session?.Send(message);

    private static RemoteMouseButton Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RemoteMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
        _ => RemoteMouseButton.None,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        _dragStart = p;
        _originAtDragStart = _origin;

        if (HorizontalTrack.Inflate(3).Contains(p) || VerticalTrack.Inflate(3).Contains(p))
        {
            var horizontal = HorizontalTrack.Inflate(3).Contains(p);
            var handle = Handle(horizontal);
            if (!handle.Contains(p))
            {
                // Clicking the track jumps the handle's centre there.
                _dragStart = horizontal ? new Point(handle.Center.X, p.Y) : new Point(p.X, handle.Center.Y);
                ScrollBarDrag(horizontal, p);
                _dragStart = p;
                _originAtDragStart = _origin;
            }
            _drag = horizontal ? Drag.HorizontalBar : Drag.VerticalBar;
        }
        else if (point.Properties.IsMiddleButtonPressed || !ContentRect.Contains(p) || _bitmap == null)
        {
            _drag = Drag.Pan;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else
        {
            _drag = Drag.Forward;
            Focus();
            _inputClient.MoveCaret(new Rect(p, new Size(1, 16 * Zoom)));
            var d = ToDesign(p);
            Forward(new PointerPressedEventMessage
            {
                X = d.X, Y = d.Y, Button = Button(point.Properties.PointerUpdateKind),
                Modifiers = PreviewSession.Modifiers(e.KeyModifiers, point.Properties),
            });
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        switch (_drag)
        {
            case Drag.Pan:
                _origin = _originAtDragStart + (p - _dragStart);
                InvalidateVisual();
                return;
            case Drag.HorizontalBar:
            case Drag.VerticalBar:
                ScrollBarDrag(_drag == Drag.HorizontalBar, p);
                return;
        }
        var inside = ContentRect.Contains(p);
        if (inside || _drag == Drag.Forward || _pointerInside)
        {
            var d = ToDesign(p);
            Forward(new PointerMovedEventMessage { X = d.X, Y = d.Y, Modifiers = PreviewSession.Modifiers(e.KeyModifiers, point.Properties) });
        }
        _pointerInside = inside;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        if (_drag == Drag.Forward)
        {
            var d = ToDesign(point.Position);
            Forward(new PointerReleasedEventMessage
            {
                X = d.X, Y = d.Y, Button = Button(point.Properties.PointerUpdateKind),
                Modifiers = PreviewSession.Modifiers(e.KeyModifiers, point.Properties),
            });
        }
        _drag = Drag.None;
        Cursor = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_pointerInside && _drag != Drag.Forward)
        {
            // Move the host's pointer off the design so hover states clear.
            Forward(new PointerMovedEventMessage { X = -1, Y = -1, Modifiers = [] });
            _pointerInside = false;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _zoomAnchor = p;
            Zoom = Math.Round(Zoom * Math.Pow(1.1, e.Delta.Y), 3);
        }
        else if (ContentRect.Contains(p) && _bitmap != null)
        {
            var d = ToDesign(p);
            Forward(new ScrollEventMessage { X = d.X, Y = d.Y, DeltaX = e.Delta.X, DeltaY = e.Delta.Y, Modifiers = PreviewSession.Modifiers(e.KeyModifiers) });
        }
        else
        {
            var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? new Vector(e.Delta.Y, 0) : e.Delta;
            _origin += delta * 50;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+0 / Ctrl+= / Ctrl+- zoom the canvas; everything else goes to the design.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D0 or Key.NumPad0 or Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)
        {
            Zoom = e.Key is Key.D0 or Key.NumPad0 ? 1 : e.Key is Key.OemMinus or Key.Subtract ? Zoom / 1.25 : Zoom * 1.25;
            e.Handled = true;
            return;
        }
        if (_session == null) { base.OnKeyDown(e); return; }
        _session.Send(PreviewSession.KeyMessage(true, e.Key, e.PhysicalKey, e.KeySymbol, e.KeyModifiers));
        // A key that may type a character must stay unhandled: the platform (X11, Win32) only raises the TextInput
        // that follows when its KeyDown was not handled, and that TextInput is what puts text in a TextBox.
        // Keys that never type (Tab, arrows, shortcuts) are handled so they don't move focus out of the preview.
        // Decided by key, not KeySymbol: X11 reports no symbol for shifted keys such as Shift+1 → "!".
        e.Handled = !MayTypeText(e);
    }

    private static bool MayTypeText(KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return false;
        if (e.KeySymbol is { Length: > 0 } symbol && char.IsControl(symbol[0])) return false;
        return e.Key is not (Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Escape or Key.Enter or Key.Back or Key.Delete or Key.Insert
            or Key.Apps or >= Key.F1 and <= Key.F24);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_session == null) { base.OnKeyUp(e); return; }
        _session.Send(PreviewSession.KeyMessage(false, e.Key, e.PhysicalKey, e.KeySymbol, e.KeyModifiers));
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (_session != null && !string.IsNullOrEmpty(e.Text))
        {
            _session.Send(new TextInputEventMessage { Text = e.Text });
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    private readonly CanvasInputClient _inputClient;

    /// <summary>
    /// Tells the platform the canvas takes text, which turns the input method on for it: without a client, IBus/fcitx
    /// never see the keys, so the compose key and dead keys (á, ñ, ü…) do nothing. Composed text arrives as
    /// TextInput and is forwarded like any other. No preedit or surrounding text: the real TextBox is in the host.
    /// </summary>
    private sealed class CanvasInputClient(PreviewSurface owner) : TextInputMethodClient
    {
        private Rect _caret;

        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => _caret;
        public override TextSelection Selection { get; set; }

        /// <summary>Where the input method's candidate/compose popup should appear: the last click in the design.</summary>
        public void MoveCaret(Rect caret)
        {
            _caret = caret;
            RaiseCursorRectangleChanged();
        }
    }
}
