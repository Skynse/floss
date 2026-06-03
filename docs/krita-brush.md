COMPLETE ANALYSIS OF KRITA'S DAB RENDERING PIPELINE
Files Read (with absolute paths):
Brush paintop directory (/home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/):
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/kis_brushop.h (92 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/kis_brushop.cpp (407 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingJob.h (77 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingJob.cpp (160 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingExecutor.h (51 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingExecutor.cpp (78 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingQueue.h (66 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingQueue.cpp (436 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingQueueCache.h (37 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisDabRenderingQueueCache.cpp (33 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisBrushOpResources.h (31 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisBrushOpResources.cpp (86 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisBrushOpSettings.h (27 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/KisBrushOpSettings.cpp (53 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/kis_brushop_settings_widget.h (33 lines)
- /home/neckles/projects/krita/plugins/paintops/defaultpaintops/brush/kis_brushop_settings_widget.cpp (101 lines)
libpaintop directory (/home/neckles/projects/krita/plugins/paintops/libpaintop/):
- KisDabCacheUtils.h (118 lines) and .cpp (108 lines)
- kis_dab_cache_base.h (112 lines) and .cpp (288 lines)
- kis_dab_cache.h (101 lines) and .cpp (234 lines)
- kis_paintop_utils.h (201 lines, in /home/neckles/projects/krita/libs/image/brushengine/)
- kis_paintop_plugin_utils.h (103 lines)
- kis_brush_based_paintop.h (92 lines) and .cpp (209 lines)
- kis_brush_based_paintop_settings.h (73 lines) and .cpp (374 lines)
- kis_brush_based_paintop_options_widget.h (35 lines) and .cpp (36 lines)
- kis_precision_option.h (52 lines) and .cpp (77 lines)
- kis_color_source.h (130 lines) and .cpp (261 lines)
- kis_color_source_option.h (31 lines) and .cpp (68 lines)
- kis_brush_option.h (41 lines) and .cpp (119 lines)
- kis_texture_option.h (81 lines) and .cpp (397 lines)
- KisSharpnessOption.h (37 lines) and .cpp (98 lines)
- KisStandardOptions.h (54 lines) and .cpp (7 lines)
- KisFlowOpacityOption.h (26 lines) and .cpp (49 lines)
- KisMirrorOption.h (28 lines) and .cpp (56 lines)
- KisOpacityOption.h (28 lines) and .cpp (30 lines)
- KisCurveOption.h (75 lines) and .cpp (204 lines)
- KisSpacingOption.h (34 lines) and .cpp (41 lines)
- KisAirbrushOptionData.h (32 lines) and .cpp (27 lines)
- KisRotationOption.h (29 lines) and .cpp (64 lines)
- KisScatterOption.h (28 lines) and .cpp (64 lines)
- KisDarkenOption.h (25 lines) and .cpp (60 lines)
- KisHSVOption.h (35 lines) and .cpp (74 lines)
- KisMaskingBrushOption.h (44 lines) and .cpp (300 lines)
- KisMaskingBrushOptionProperties.h (39 lines) and .cpp (74 lines)
- KisMirrorProperties.h (26 lines)
- KisTextureMaskInfo.h (85 lines) and .cpp (273 lines)
- kis_current_outline_fetcher.h (60 lines) and .cpp (156 lines)
- kis_outline_generation_policy.h (50 lines)
- kis_simple_paintop_factory.h (243 lines)
- KisStandardOptionData.h (158 lines) and .cpp (209 lines)
- KisCurveOptionData.h (38 lines)
- KisCurveOptionDataCommon.h (86 lines) and .cpp (71 lines)
- KisSpacingOptionData.h (41 lines)
- KisMirrorOptionData.h (40 lines)
- KisDynamicSensorIds.h (38 lines)
- KisDynamicSensorFactory.h (33 lines) and .cpp (12 lines)
- KisKritaSensorPack.h (97 lines) and .cpp (285 lines)
- KisSensorData.h (82 lines) and .cpp (142 lines)
- KisSensorPackInterface.h (39 lines) and .cpp (16 lines)
- KisPaintOpOptionUtils.h (22 lines)
- KisBrushOptionWidgetFlags.h (22 lines)
- KisOptionTuple.h (179 lines)
- KisPrefixedOptionDataWrapper.h (53 lines)
- kritapaintop_export_instance.h (27 lines)
1. HOW DABS ARE CREATED AND RENDERED
The entire dab pipeline starts at KisBrushOp::paintAt() (kis_brushop.cpp, lines 103-150):
paintAt(info) -> 
  1. Compute size/rotation/ratio via options (m_sizeOption.apply(info), etc.)
  2. Compute scatter offset via m_scatterOption.apply()
  3. Compute opacity/flow via m_opacityOption.apply()
  4. Package into DabRequestInfo {color, cursorPos, shape, info, softness, lightness}
  5. Call m_dabExecutor->addDab(request, opacity, flow)
  6. Return spacing info for the next dab
DabRequestInfo (KisDabCacheUtils.h, lines 56-82) bundles:
- color (KoColor), cursorPoint (QPointF), shape (KisDabShape), info (KisPaintInformation)
- softnessFactor, lightnessStrength
DabGenerationInfo (KisDabCacheUtils.h, lines 84-97) is the resolved output of cache lookup:
- mirrorProperties, shape, dstDabRect, subPixel
- solidColorFill (bool), paintColor, info
- softnessFactor, lightnessStrength, needsPostprocessing
The actual mask generation happens in KisDabCacheUtils::generateDab() (KisDabCacheUtils.cpp, lines 45-91):
- If brushApplication() == IMAGESTAMP: calls brush->paintDevice() to get an image stamp
- If solidColorFill: calls brush->mask() with a single paint color
- Otherwise (gradient/pattern coloring): calls colorSource->colorize() to fill a paint device, then brush->mask() with that device
- After mask generation: applies mirroring via (*dab)->mirror()
2. COLOR APPLICATION TO MASKS
Color sources are managed via KisColorSource hierarchy (kis_color_source.h). The concrete types:
- KisUniformColorSource (base): Stores a KoColor m_color
- KisPlainColorSource: Mixes foreground/background colors (lines 70-101 of .cpp)
- KisGradientColorSource: Samples from a gradient at mix point
- KisUniformRandomColorSource: Random uniform color per dab
- KisTotalRandomColorSource: Random per-pixel coloring
- KoPatternColorSource: Fills from a pattern device
In KisBrushOpResources::syncResourcesToSeqNo() (KisBrushOpResources.cpp, lines 73-86):
1. colorSource->selectColor(mixOption, info) - picks the base color
2. m_darkenOption.apply(colorSource, info) - applies darken transform
3. HSV transformations if enabled
4. Then calls parent's DabRenderingResources::syncResourcesToSeqNo(seqNo, info) which calls brush->prepareForSeqNo(info, seqNo)
How color reaches the mask:
- solidColorFill path: The paintColor from DabGenerationInfo is passed directly to brush->mask(device, paintColor, shape, ...) which stamps the brush alpha multiplied by that color
- non-uniform path: colorSource->colorize(device, rect, pos) fills a temporary paint device with the color source's colors, then brush->mask(device, colorSourceDevice, shape, ...) stamps brush alpha over that device
3. THE DAB CACHING SYSTEM
Three layers of caching:
Layer 1: Dab cache base (kis_dab_cache_base.h/cpp)
- KisDabCacheBase manages the last dab parameters (SavedDabParameters) and precision levels
- fetchDabGenerationInfo() (lines 231-287 of .cpp):
1. Computes DabPosition (rect + subPixel + angle) via calculateDabRect()
2. Checks if the brush supportsCaching() and if color source is uniform
3. Builds SavedDabParameters from the new request
4. Compares against last saved using precisionLevels[] tolerance table (lines 32-38)
5. If parameters match within tolerance: *shouldUseCache = true
6. Otherwise: saves new params and *shouldUseCache = false
Precision levels (lines 32-38 of .cpp):
Level 1: angle=1°/180, size=5%, subPixel=1, softness=0.01, ratio=5%  (fastest)
Level 2: angle=1°/180, size=1%, subPixel=1, softness=0.01, ratio=1%
Level 3: angle=1°/180, size=0,   subPixel=1, softness=0.01, ratio=eps
Level 4: angle=1°/180, size=0,   subPixel=0.5, softness=0.01, ratio=eps
Level 5: angle=eps,   size=0,   subPixel=eps, softness=eps, ratio=eps  (highest quality)
Effective level auto-selects level 5 for small dabs (< 30px) or when no imprecise options, else level 3.
Layer 2: KisDabCache (kis_dab_cache.h/cpp) - used by older paintops
- Holds m_d->dab and m_d->dabOriginal as cached paint devices
- fetchDabCommon() (lines 153-234):
1. Prepares TemporaryResourcesWithoutOwning with brush + options
2. Calls fetchDabGenerationInfo() from base class
3. If shouldUseCache: returns fetchFromCache() which optionally re-runs postprocessing
4. If cache miss: calls generateDab() followed by optional postProcessDab()
Layer 3: KisDabRenderingQueueCache (KisDabRenderingQueueCache.h/cpp)
- Implements KisDabRenderingQueue::CacheInterface + inherits KisDabCacheBase
- getDabType() delegates to fetchDabGenerationInfo() (line 27)
- Tells the queue whether to use a Copy job (cache hit, no postprocessing), Postprocess job (cache hit, needs postprocessing), or Dab job (cache miss)
4. HOW STAMPS ARE COMPOSITED ONTO THE CANVAS
The compositing flow in KisBrushOp::doAsynchronousUpdate() (kis_brushop.cpp, lines 207-370):
1. Fetch completed dabs: m_dabExecutor->takeReadyDabs() returns QList<KisRenderedDab>
- Each KisRenderedDab contains: device (KisFixedPaintDeviceSP), offset (QPoint), opacity, flow, averageOpacity
2. Wrap-around handling (lines 244-283): If wrap-around mode is active, each dab is duplicated with offset adjustments for each wrap region
3. Split into rects: KisPaintOpUtils::splitDabsIntoRects() (kis_paintop_utils.h, line 197) partitions the dirty area into non-overlapping rectangles for parallel processing
4. Parallel blitting (lines 297-303): For each rect, a concurrent job is created:
KritaUtils::addJobConcurrent(jobs,
    [rc, state] () {
        state->painter->bltFixed(rc, state->dabsQueue);
    });
5. Mirroring (lines 166-205, addMirroringJobs()):
- For each mirror direction (horizontal, vertical, or both), dabs are mirrored via state->painter->mirrorDab()
- Then blitted via state->painter->bltFixed()
- Uses sequential/concurrent job ordering for correct ordering
6. Dirty rect notification (lines 323-364): A final sequential job marks all dirty rects via state->painter->addDirtyRect(), computes performance metrics, and adjusts the update period for the next frame
5. THE FLOW FROM paintAt() TO SCREEN PIXELS
Complete call chain:
KisBrushOp::paintAt(info)
  ├── m_sizeOption.apply(info) → scale
  ├── m_rotationOption.apply(info) → rotation
  ├── m_ratioOption.apply(info) → ratio
  ├── KisDabShape(scale, ratio, rotation)
  ├── m_scatterOption.apply(info, width, height) → cursorPos
  ├── m_opacityOption.apply(info, &dabOpacity, &dabFlow)
  ├── DabRequestInfo(color, cursorPos, shape, info, softness, lightness)
  └── KisDabRenderingExecutor::addDab(request, opacity, flow)
        │
        ├── KisDabRenderingQueue::addDab(request, opacity, flow)
        │     │
        │     ├── Allocates seqNo
        │     ├── Fetches/takes resources from cache pool
        │     ├── syncResourcesToSeqNo() [color selection + brush prep]
        │     ├── cacheInterface->getDabType() → Cache check
        │     │     └── KisDabRenderingQueueCache::getDabType()
        │     │           └── KisDabCacheBase::fetchDabGenerationInfo()
        │     │                 ├── calculateDabRect() → dstDabRect, subPixel
        │     │                 ├── Check solidColorFill (uniform vs non-uniform source)
        │     │                 ├── Build SavedDabParameters
        │     │                 ├── Compare with last cached params at precision level
        │     │                 ├── Set needsPostprocessing (texture || sharpness)
        │     │                 └── Return shouldUseCache flag
        │     │
        │     ├── Determines job type: Dab | Postprocess | Copy
        │     ├── Appends job to queue
        │     └── Returns jobToRun if status == Running
        │
        ├── If job ready: creates KisDabRenderingJobRunner (QRunnable)
        └── Adds to runnableJobsInterface for concurrent execution
              │
              └── KisDabRenderingJobRunner::run()
                    │
                    ├── m_parentQueue->fetchResourcesFromCache()
                    ├── executeOneJob(job, resources, queue)
                    │     │
                    │     ├── resources->syncResourcesToSeqNo(seqNo, info)
                    │     │     └── colorSource->selectColor() → select base color
                    │     │     └── darkenOption.apply() → darken
                    │     │     └── HSV transforms → modify color
                    │     │     └── brush->prepareForSeqNo() → brush prep
                    │     │
                    │     ├── If Dab job:
                    │     │     ├── fetchCachedPaintDevice() → new KisFixedPaintDevice
                    │     │     └── KisDabCacheUtils::generateDab(di, resources, &device)
                    │     │           │
                    │     │           ├── IMAGESTAMP? → brush->paintDevice()
                    │     │           ├── solidColorFill? → brush->mask(device, color, shape, ...)
                    │     │           │     └── Stamps brush alpha × paint color into device
                    │     │           └── else → colorSource->colorize() + brush->mask(device, csDevice, shape, ...)
                    │     │
                    │     ├── If needsPostprocessing:
                    │     │     ├── Copy original device
                    │     │     └── KisDabCacheUtils::postProcessDab()
                    │     │           ├── sharpnessOption->applyThreshold() → binary/soft threshold
                    │     │           └── textureOption->apply(dab, topLeft, info) → texture composite
                    │     │
                    │     └── Copy: just clone original→postprocessed from completed source job
                    │
                    ├── notifyJobFinished(seqNo) → triggers dependent Copy/Postprocess jobs
                    └── putResourcesToCache(resources)

... later, in the UI update cycle ...

KisBrushOp::doAsynchronousUpdate(jobs)
  ├── m_dabExecutor->takeReadyDabs(mutable, limit, &someLeft)
  │     └── KisDabRenderingQueue::takeReadyDabs()
  │           ├── Iterates completed jobs
  │           ├── Builds KisRenderedDab{device, offset, opacity, flow}
  │           ├── Tracks averageOpacity for correct compositing
  │           └── Returns list
  │
  ├── Split rects via KisPaintOpUtils::splitDabsIntoRects()
  ├── Add concurrent jobs: painter->bltFixed(rect, dabsQueue)
  │     └── KisPainter composites each dab in the rect onto canvas
  ├── Mirror dabs if needed: painter->mirrorDab() + bltFixed()
  └── Final sequential job: painter->addDirtyRect() for each rect
        └── Updates averageOpacity for proper blending
Key Job Types in the Queue (KisDabRenderingJob.h, lines 22-26):
Type	Meaning
Dab	Full render from scratch
Postprocess	Copy cached original + reapply postprocessing
Copy	Direct copy of cached result
Resource Pooling:
- KisDabRenderingQueue::Private::cachedResources holds a pool of DabRenderingResources* objects
- Resources are fetched/taken via fetchResourcesFromCache() / putResourcesToCache() (lines 398-415)
- paintDeviceAllocator is a KisOptimizedByteArray::PooledMemoryAllocator for efficient paint device allocation (line 51, 372)
