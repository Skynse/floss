using Floss.App.Docking;
using Floss.App.Features;
using Floss.App.Features.Dock.Panels;
using Floss.App.Features.Overview.Histogram;

namespace Floss.App.Features.Dock.BuiltIn;

/// <summary>RGB histogram docker — uses <see cref="IDocumentHistogramSource"/>.</summary>
public sealed class HistogramDockFeature : IFeatureModule
{
    public const string PanelId = "histogram";

    public void Register(IFeatureSession session)
    {
        var histogram = session.GetService<IDocumentHistogramSource>();
        DockFeature.Register(
            PanelId,
            "Histogram",
            () => new HistogramDockPanel(session, histogram),
            defaultZone: "right-0",
            proportion: 0.12,
            minHeight: 96,
            sizing: DockPanelSizing.Fill);
    }
}
