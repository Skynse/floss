using System;

namespace Floss.App.Features.Overview.Histogram;

/// <summary>
/// Debounced RGB histogram of the flattened active document (same composite as the navigator).
/// </summary>
public interface IDocumentHistogramSource
{
    /// <summary>Fired on the UI thread when bins are ready, or null when unavailable.</summary>
    event Action<DocumentHistogram?>? HistogramReady;

    /// <summary>Queue recompute (coalesced, stroke-aware).</summary>
    void RequestUpdate();

    void CancelPending();
}
