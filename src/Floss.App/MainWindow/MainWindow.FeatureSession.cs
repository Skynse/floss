#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Floss.App.Canvas;
using Floss.App.Config;
using Floss.App.Document;
using Avalonia.Input;
using Floss.App.Features;
using Floss.App.Features.Actions;
using Floss.App.Features.Dock.Panels;
using Floss.App.Tools;

namespace Floss.App;

public partial class MainWindow : IFeatureSession, ICanvasViewHost
{
    private FeatureServices? _featureServices;
    private event Action? _activeCanvasChanged;

    AppConfig IFeatureSession.Config => App.Config;

    DrawingCanvas IFeatureSession.ActiveCanvas => _canvas;

    DrawingDocument IFeatureSession.ActiveDocument => _canvas.Document;

    ICanvasViewHost IFeatureSession.View => this;

    event Action? IFeatureSession.ActiveCanvasChanged
    {
        add => _activeCanvasChanged += value;
        remove => _activeCanvasChanged -= value;
    }

    T IFeatureSession.GetService<T>() => EnsureFeatureServices().Get<T>();

    T? IFeatureSession.TryGetService<T>() where T : class => EnsureFeatureServices().TryGet<T>();

    void IFeatureSession.RequestDockerRebuild() => RebuildDockers();

    private bool TryExecuteRegisteredAction(Key key, KeyModifiers modifiers)
        => _featureServices?.TryGet<IActionRegistry>()?.TryExecute(key, modifiers, CanExecuteCanvasDocumentShortcut)
           == true;

    /// <summary>Creates default services and registers dock modules (once at startup).</summary>
    private void InitializeFeatureSession()
    {
        var services = EnsureFeatureServices();
        RegisterSessionCommands(services);
        FeatureModuleLoader.RegisterServices(this, services);

        BuildToolRail();
        RefreshToolProperties();

        FeatureModuleLoader.RegisterAll(this);
    }

    private FeatureServices EnsureFeatureServices()
    {
        if (_canvas is null)
            throw new InvalidOperationException("Feature session requires an active DrawingCanvas before initialization.");

        return _featureServices ??= FeatureSessionBootstrap.Create(_canvas, this);
    }

    /// <summary>Single tab-switch hook for all canvas-bound feature services.</summary>
    private void SyncFeatureSessionToActiveCanvas()
    {
        EnsureFeatureServices().NotifyActiveCanvas(_canvas);
        _activeCanvasChanged?.Invoke();
    }

    bool ICanvasViewHost.HasDocument => _canvas.HasDocument;

    int ICanvasViewHost.DocumentWidth => _canvas.HasDocument ? _canvas.Document.Width : 0;

    int ICanvasViewHost.DocumentHeight => _canvas.HasDocument ? _canvas.Document.Height : 0;

    double ICanvasViewHost.Zoom => _zoom;

    double ICanvasViewHost.PanOffsetX => _canvasPan.X;

    double ICanvasViewHost.PanOffsetY => _canvasPan.Y;

    double ICanvasViewHost.Rotation => _rotation;

    int ICanvasViewHost.FlipX => (int)_canvasFlip.ScaleX;

    int ICanvasViewHost.FlipY => (int)_canvasFlip.ScaleY;

    double ICanvasViewHost.ViewportWidth => _canvas.ViewportWidth;

    double ICanvasViewHost.ViewportHeight => _canvas.ViewportHeight;

    PixelRegion? ICanvasViewHost.VisibleDocumentRegion => _canvas.VisibleDocumentRegion;

    void ICanvasViewHost.PanBy(double dx, double dy) => ((IViewportController)this).PanBy(dx, dy);

    void ICanvasViewHost.ZoomBy(double factor, Point viewportCenter) => ((IViewportController)this).ZoomBy(factor, viewportCenter);

    void ICanvasViewHost.ResetView() => ResetView();

    public event Action? ViewTransformChanged;
    public event Action? DocumentVisualChanged;

    private void NotifyViewTransformChanged() => ViewTransformChanged?.Invoke();

    private void WireFeatureSessionDocumentEvents()
    {
        var doc = _canvas.Document;
        doc.StrokeSuspendEnded += OnFeatureSessionDocumentVisualChanged;
        doc.LayersChanged += OnFeatureSessionDocumentVisualChanged;
        doc.LayerMetadataChanged += OnFeatureSessionDocumentVisualChanged;
        doc.SelectionChanged += OnFeatureSessionDocumentVisualChanged;
    }

    private void UnwireFeatureSessionDocumentEvents()
    {
        var doc = _canvas.Document;
        doc.StrokeSuspendEnded -= OnFeatureSessionDocumentVisualChanged;
        doc.LayersChanged -= OnFeatureSessionDocumentVisualChanged;
        doc.LayerMetadataChanged -= OnFeatureSessionDocumentVisualChanged;
        doc.SelectionChanged -= OnFeatureSessionDocumentVisualChanged;
    }

    private void OnFeatureSessionDocumentVisualChanged(object? sender, EventArgs e)
        => DocumentVisualChanged?.Invoke();
}
