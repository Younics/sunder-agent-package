using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

internal sealed class MemoryGraphControl : Control
{
    public static readonly StyledProperty<MemoryGraphState?> GraphProperty = AvaloniaProperty.Register<MemoryGraphControl, MemoryGraphState?>(nameof(Graph));
    public static readonly StyledProperty<Guid?> SelectedMemoryIdProperty = AvaloniaProperty.Register<MemoryGraphControl, Guid?>(nameof(SelectedMemoryId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> SurfaceProperty = AvaloniaProperty.Register<MemoryGraphControl, IBrush?>(nameof(Surface));
    public static readonly StyledProperty<IBrush?> NodeSurfaceProperty = AvaloniaProperty.Register<MemoryGraphControl, IBrush?>(nameof(NodeSurface));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<MemoryGraphControl, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<IBrush?> SecondaryProperty = AvaloniaProperty.Register<MemoryGraphControl, IBrush?>(nameof(Secondary));
    public static readonly StyledProperty<IBrush?> LineProperty = AvaloniaProperty.Register<MemoryGraphControl, IBrush?>(nameof(Line));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<MemoryGraphControl, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<double> NodeWidthProperty = AvaloniaProperty.Register<MemoryGraphControl, double>(nameof(NodeWidth), 220);
    public static readonly StyledProperty<double> NodeHeightProperty = AvaloniaProperty.Register<MemoryGraphControl, double>(nameof(NodeHeight), 84);
    public static readonly StyledProperty<double> TextSizeProperty = AvaloniaProperty.Register<MemoryGraphControl, double>(nameof(TextSize), 14);
    private readonly Dictionary<string, Point> _positions = new(StringComparer.Ordinal);
    private Vector _offset;
    private double _zoom = 1;
    private Point _lastPointer;
    private string? _dragNode;
    private bool _panning;
    private int _nextSlot;
    private Guid? _sessionId;
    private bool _initialFit;
    public MemoryGraphState? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public Guid? SelectedMemoryId { get => GetValue(SelectedMemoryIdProperty); set => SetValue(SelectedMemoryIdProperty, value); }
    public IBrush? Surface { get => GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public IBrush? NodeSurface { get => GetValue(NodeSurfaceProperty); set => SetValue(NodeSurfaceProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
    public IBrush? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double NodeWidth { get => GetValue(NodeWidthProperty); set => SetValue(NodeWidthProperty, value); }
    public double NodeHeight { get => GetValue(NodeHeightProperty); set => SetValue(NodeHeightProperty, value); }
    public double TextSize { get => GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
    internal double Zoom => _zoom;
    internal Vector ViewOffset => _offset;
    internal IReadOnlyDictionary<string, Point> Positions => _positions;

    static MemoryGraphControl() => AffectsRender<MemoryGraphControl>(GraphProperty, SelectedMemoryIdProperty,
        SurfaceProperty, NodeSurfaceProperty, ForegroundProperty, SecondaryProperty, LineProperty, AccentProperty,
        NodeWidthProperty, NodeHeightProperty, TextSizeProperty);

    public MemoryGraphControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Bind(SurfaceProperty, this.GetResourceObservable("Sunder.Brush.Surface.Base"));
        Bind(NodeSurfaceProperty, this.GetResourceObservable("Sunder.Brush.Surface.Popover"));
        Bind(ForegroundProperty, this.GetResourceObservable("Sunder.Brush.Foreground.Primary"));
        Bind(SecondaryProperty, this.GetResourceObservable("Sunder.Brush.Foreground.Secondary"));
        Bind(LineProperty, this.GetResourceObservable("Sunder.Brush.Border.Strong"));
        Bind(AccentProperty, this.GetResourceObservable("Sunder.Brush.Accent"));
        Bind(NodeWidthProperty, this.GetResourceObservable("Sunder.Agent.Graph.NodeWidth"));
        Bind(NodeHeightProperty, this.GetResourceObservable("Sunder.Agent.Graph.NodeHeight"));
        Bind(TextSizeProperty, this.GetResourceObservable("Sunder.FontSize.Body"));
        Avalonia.Automation.AutomationProperties.SetName(this, "Memory connections. Drag to arrange, scroll to zoom. Inspect selected memories in the Data tab.");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != GraphProperty) return;
        if (Graph?.SessionId is { } session && session != _sessionId)
        {
            _sessionId = session;
            _positions.Clear(); _nextSlot = 0; _offset = default; _zoom = 1; _initialFit = false;
            _dragNode = null; _panning = false;
        }
        foreach (var node in Graph?.Nodes ?? [])
        {
            if (_positions.ContainsKey(node.Key)) continue;
            var index = _nextSlot++;
            _positions[node.Key] = new Point((index % 4) * NodeWidth * 1.35, (index / 4) * NodeHeight * 1.7);
        }
        if (!_initialFit && Bounds.Width > 0 && Bounds.Height > 0 && Graph?.Nodes.Count > 0) { Fit(); _initialFit = true; }
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_initialFit && Graph?.Nodes.Count > 0 && Bounds.Width > 0 && Bounds.Height > 0)
        {
            Fit(); _initialFit = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Surface is { } surface) context.FillRectangle(surface, Bounds.WithX(0).WithY(0));
        if (Graph is not { } graph) return;
        var selected = SelectedMemoryId is { } id ? graph.Nodes.FirstOrDefault(node => node.MemoryId == id)?.Key : null;
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset)))
        {
            foreach (var edge in graph.Edges)
            {
                if (!_positions.TryGetValue(edge.From, out var from) || !_positions.TryGetValue(edge.To, out var to)) continue;
                var emphasized = selected is not null && (edge.From == selected || edge.To == selected);
                var brush = emphasized ? Accent : Line;
                var a = from + new Vector(NodeWidth / 2, NodeHeight / 2);
                var b = to + new Vector(NodeWidth / 2, NodeHeight / 2);
                context.DrawLine(new Pen(brush, emphasized ? 2 : 1), a, b);
                var direction = new Vector(b.X - a.X, b.Y - a.Y);
                if (direction.Length > 0)
                {
                    direction /= direction.Length;
                    var distance = Math.Min(direction.X == 0 ? double.PositiveInfinity : NodeWidth / (2 * Math.Abs(direction.X)),
                        direction.Y == 0 ? double.PositiveInfinity : NodeHeight / (2 * Math.Abs(direction.Y)));
                    var tip = b - direction * distance;
                    var side = new Vector(-direction.Y, direction.X) * 5;
                    context.DrawLine(new Pen(brush), tip, tip - direction * 10 + side);
                    context.DrawLine(new Pen(brush), tip, tip - direction * 10 - side);
                }
                if (emphasized) DrawText(context, edge.Label, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), Secondary);
            }
            var visible = new Rect(ToWorld(default), new Size(Bounds.Width / _zoom, Bounds.Height / _zoom));
            foreach (var node in graph.Nodes)
            {
                if (!_positions.TryGetValue(node.Key, out var point)) continue;
                var rect = new Rect(point, new Size(NodeWidth, NodeHeight));
                if (!rect.Intersects(visible)) continue;
                var radius = this.TryFindResource("Sunder.Radius.Small", out var resource) && resource is CornerRadius corner ? corner.TopLeft : 0;
                context.DrawRectangle(NodeSurface, new Pen(node.Key == selected ? Accent : Line, node.Key == selected ? 2 : 1), rect, radius, radius);
                using (context.PushClip(rect.Deflate(TextSize * .7)))
                {
                    DrawText(context, node.Title, point + new Vector(TextSize, TextSize * .6), node.Key == selected ? Accent : Secondary);
                    DrawText(context, Preview(node.Preview), point + new Vector(TextSize, TextSize * 2.2), Foreground, lines: 2);
                }
            }
        }
    }

    private void DrawText(DrawingContext context, string value, Point point, IBrush? brush, int lines = 1)
    {
        if (brush is null) return;
        context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), TextSize, brush)
        {
            MaxTextWidth = Math.Max(1, NodeWidth - TextSize * 2),
            MaxLineCount = lines,
            Trimming = TextTrimming.CharacterEllipsis,
        }, point);
    }
    private static string Preview(string value)
    {
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 48 ? text[..48] + "…" : text;
    }
    private Point ToWorld(Point point) => new((point.X - _offset.X) / _zoom, (point.Y - _offset.Y) / _zoom);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _lastPointer = e.GetPosition(this);
        var world = ToWorld(_lastPointer);
        var node = Graph?.Nodes.Reverse().FirstOrDefault(node => _positions.TryGetValue(node.Key, out var point)
            && new Rect(point, new Size(NodeWidth, NodeHeight)).Contains(world));
        _dragNode = node?.Key;
        _panning = node is null;
        if (node?.MemoryId is { } id) SetCurrentValue(SelectedMemoryIdProperty, id);
        e.Pointer.Capture(this);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured != this) return;
        var current = e.GetPosition(this);
        var delta = current - _lastPointer;
        if (_dragNode is { } key && _positions.TryGetValue(key, out var position)) _positions[key] = position + delta / _zoom;
        else if (_panning) _offset += delta;
        _lastPointer = current;
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        _dragNode = null;
        _panning = false;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragNode = null;
        _panning = false;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Home) { Fit(); e.Handled = true; }
        else if (e.Key is Key.Add or Key.OemPlus) { ZoomBy(1.2); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { ZoomBy(1 / 1.2); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            _offset += e.Key switch { Key.Left => new Vector(40, 0), Key.Right => new(-40, 0), Key.Up => new(0, 40), _ => new(0, -40) };
            InvalidateVisual(); e.Handled = true;
        }
    }
    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));
    private void ZoomAt(double factor, Point point)
    {
        var world = ToWorld(point);
        _zoom = Math.Clamp(_zoom * factor, .02, 3);
        _offset = point - new Point(world.X * _zoom, world.Y * _zoom);
        InvalidateVisual();
    }
    public void Fit()
    {
        var positions = Graph?.Nodes.Where(node => _positions.ContainsKey(node.Key)).Select(node => _positions[node.Key]).ToArray() ?? [];
        if (positions.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var left = positions.Min(point => point.X);
        var top = positions.Min(point => point.Y);
        var width = positions.Max(point => point.X) + NodeWidth - left;
        var height = positions.Max(point => point.Y) + NodeHeight - top;
        _zoom = Math.Clamp(Math.Min(Bounds.Width / (width + NodeWidth * .2), Bounds.Height / (height + NodeHeight * .2)), .02, 1.5);
        _offset = new Vector((Bounds.Width - width * _zoom) / 2 - left * _zoom, (Bounds.Height - height * _zoom) / 2 - top * _zoom);
        InvalidateVisual();
    }
    public void ResetLayout()
    {
        _positions.Clear(); _nextSlot = 0;
        var graph = Graph;
        SetCurrentValue(GraphProperty, null);
        SetCurrentValue(GraphProperty, graph);
        Fit();
    }
}
