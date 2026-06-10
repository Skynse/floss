using System;
using Floss.App.Canvas;
using Floss.App.Features.Actions;
using Floss.App.Features.Menu;
using Floss.App.Features.Overview;
using Floss.App.Features.Overview.Histogram;
using Floss.App.Features.Overlays;
using Floss.App.Features.Tools;

namespace Floss.App.Features;

/// <summary>Registers default in-tree feature services. Add new services here — not on <see cref="IFeatureSession"/>.</summary>
public static class FeatureSessionBootstrap
{
    public static FeatureServices Create(DrawingCanvas initialCanvas, ICanvasViewHost? viewHost = null)
    {
        ArgumentNullException.ThrowIfNull(initialCanvas);

        var services = new FeatureServices();
        var overviewCache = new DocumentOverviewCache(initialCanvas);
        services.Register(overviewCache);
        services.Register<IDocumentOverviewSource>(new DocumentOverviewSource(overviewCache));
        services.Register<IDocumentHistogramSource>(new DocumentHistogramSource(overviewCache));
        services.Register<IDocumentHistorySource>(new DocumentHistorySource(initialCanvas));
        services.Register<IDocumentEvents>(new DocumentEventsSource(initialCanvas, viewHost));
        services.Register<IMenuRegistry>(new MenuRegistry());
        services.Register<IActionRegistry>(new ActionRegistry());
        services.Register<ICanvasOverlayRegistry>(new CanvasOverlayRegistry());
        services.Register<IToolRegistry>(new ToolRegistry());
        return services;
    }
}
