using System;

namespace Floss.App.Features.Overview;

/// <summary>
/// Provides debounced, downscaled snapshots of the active document for navigator and future dockers.
/// </summary>
public interface IDocumentOverviewSource
{
    /// <summary>Fired on the UI thread when a requested snapshot is ready, or null when unavailable.</summary>
    event Action<DocumentOverviewSnapshot?>? SnapshotReady;

    /// <summary>
    /// Queue a full-document composite that fits within <paramref name="maxWidth"/>×<paramref name="maxHeight"/>.
    /// Latest request wins; work is coalesced.
    /// </summary>
    void RequestSnapshot(int maxWidth, int maxHeight);

    /// <summary>Cancel any in-flight or debounced snapshot work.</summary>
    void CancelPending();
}
