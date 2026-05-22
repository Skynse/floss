using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Psd;
using Floss.App.Processes;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Timelapse;
using Floss.App.Tools;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using AppKeyBinding = Floss.App.Input.KeyBinding;

namespace Floss.App.Tests;

internal static class Program
{
    private static readonly List<(string Name, Action Test)> Tests =
    [
        ("PixelRegion intersects overlapping regions", PixelRegionTests.Intersect_ReturnsOverlap),
        ("PixelRegion returns empty for touching regions", PixelRegionTests.Intersect_ReturnsEmptyForTouchingEdges),
        ("PixelRegion unions empty and non-empty regions", PixelRegionTests.Union_HandlesEmptyRegions),
        ("PixelRegion inflates, translates, and clips", PixelRegionTests.Transforms_WorkTogether),

        ("TiledPixelBuffer starts with clamped positive bounds", TiledPixelBufferTests.Constructor_ClampsMinimumSize),
        ("TiledPixelBuffer writes and reads positive and negative pixels", TiledPixelBufferTests.SetPixel_ReadsAcrossPositiveAndNegativeTiles),
        ("TiledPixelBuffer captures and restores partial regions", TiledPixelBufferTests.CaptureAndRestore_RoundTripPartialRegion),
        ("TiledPixelBuffer clears only the requested region", TiledPixelBufferTests.Clear_RemovesOnlyRequestedPixels),
        ("TiledPixelBuffer prunes fully transparent tiles", TiledPixelBufferTests.Clear_RemovesTransparentTiles),
        ("TiledPixelBuffer copies only opaque BGRA source pixels", TiledPixelBufferTests.CopyFromBgra_SkipsTransparentPixels),
        ("TiledPixelBuffer captures tiles as defensive copies", TiledPixelBufferTests.CaptureTiles_ReturnsDefensiveCopies),
        ("TiledPixelBuffer computes tight content bounds", TiledPixelBufferTests.ComputeContentBounds_ReturnsNonTransparentExtents),
        ("TiledPixelBuffer restores truncated byte arrays defensively", TiledPixelBufferTests.Restore_TruncatedBytesDoesNotThrowOrOverread),
        ("TiledPixelBuffer solid fills isolate tile mutations", TiledPixelBufferTests.FillSolid_SharedTemplatesDoNotLeakMutations),
        ("TiledPixelBuffer scratch disk round-trips tiles through disk", TiledPixelBufferTests.ScratchDisk_RoundTripsTilesThroughDisk),
        ("Layer compositor monochrome expression thresholds coverage", LayerCompositorTests.MonochromeExpression_ThresholdsCoverageBeforePaperComposite),
        ("Layer compositor samples final composited viewport color", LayerCompositorTests.SampleCompositePixel_UsesFinalCompositorResult),
        ("Layer compositor budgets dirty tile projection", LayerCompositorTests.Composite_BudgetsDirtyTiles),
        ("Layer compositor selects LOD for huge low zoom canvas", LayerCompositorTests.Composite_SelectsLodForHugeLowZoomCanvas),

        ("KeyBinding parses friendly names and modifiers", KeyBindingTests.Parse_HandlesFriendlyNamesAndModifiers),
        ("KeyBinding formats and displays shortcuts", KeyBindingTests.ToStringAndDisplay_ReturnExpectedText),
        ("KeyBinding matches modifier-only shortcuts", KeyBindingTests.Matches_HandlesModifierOnlyBindings),
        ("KeyBinding updates modifier state on key transitions", KeyBindingTests.ModifierHelpers_UpdateModifierFlags),
        ("KeyBinding JSON converter round-trips strings", KeyBindingTests.JsonConverter_RoundTrips),
        ("Brush size adjustment scales smoothly across sizes", BrushSizeAdjustmentTests.ScalesSmoothlyAcrossSizes),
        ("Brush size adjustment clamps boundaries", BrushSizeAdjustmentTests.ClampsBoundaries),

        ("Input router: modifier release does not end running stroke", CanvasInputRouterTests.ModifierReleaseDoesNotEndRunningStroke),
        ("Input router: modifier press during stroke does not change active tool", CanvasInputRouterTests.ModifierPressDuringStrokeDoesNotChangeActiveTool),
        ("Input router: completed stroke is not cancelled by later temp tool activation", CanvasInputRouterTests.CompletedStrokeIsNotCancelledByLaterTempToolActivation),
        ("Input router: after stroke release, still-held Space becomes ready/pan", CanvasInputRouterTests.AfterStrokeReleaseHeldSpaceBecomesReadyPan),
        ("Input router: Ctrl+Shift resolves through stale tool-specific None", CanvasInputRouterTests.CtrlShiftFallsThroughStaleSpecificNone),
        ("Input router: capture lost cancels only active transaction", CanvasInputRouterTests.CaptureLostCancelsOnlyActiveTransaction),

        ("CubicCurve identity and linear evaluation", BrushTests.CubicCurve_EvaluatesIdentityAndLinearCurves),
        ("CubicCurve clamps, sorts, serializes, and clones points", BrushTests.CubicCurve_PointManagementAndSerialization),
        ("SensorConfig maps raw stroke values", BrushTests.SensorConfig_RawValuesAreNormalized),
        ("CurveOption combines sensors and clones deeply", BrushTests.CurveOption_ComputesAndClones),
        ("BrushDynamics serializes and handles invalid JSON", BrushTests.BrushDynamics_SerializesAndFallsBack),
        ("Brush parameter graphs evaluate live stroke inputs", BrushTests.BrushParameterGraph_EvaluatesStrokeInputs),
        ("BrushPreset legacy dynamics bridge preserves settings", BrushTests.BrushPreset_LegacyDynamicsBridge),
        ("Brush engine reuses masks during large strokes", BrushTests.BrushEngine_ReusesMasksDuringLargeStrokes),
        ("Brush engine treats color mix as a master switch", BrushTests.BrushEngine_ColorMixOffIgnoresWetPaintFields),
        ("Brush engine applies CSP-style tip thickness", BrushTests.BrushEngine_AppliesTipThickness),
        ("Brush engine applies tip density and thickness dynamics", BrushTests.BrushEngine_AppliesTipDensityAndThicknessDynamics),
        ("Brush engine blend mode does not drag paint across empty canvas", BrushTests.BrushEngine_BlendModeDoesNotCarryPaint),
        ("Brush engine low-spacing simple brushes use cached tile-major path", BrushTests.BrushEngine_LowSpacingUsesCachedTileMajorPath),
        ("Brush engine shaped tips use cached tile-major path", BrushTests.BrushEngine_ShapedTipsUseCachedTileMajorPath),
        ("Brush engine multi-tips use cached tile-major path", BrushTests.BrushEngine_MultiTipsUseCachedTileMajorPath),
        ("Brush engine single mode ignores material tip list", BrushTests.BrushEngine_SingleModeIgnoresMaterialTipList),
        ("Brush engine color image tips use cached tile-major path", BrushTests.BrushEngine_ColorImageTipsUseCachedTileMajorPath),
        ("Brush engine dab cache survives batched unique stamps", BrushTests.BrushEngine_DabCacheSurvivesBatchedUniqueStamps),
        ("Brush engine batched segments match sequential dry stroke", BrushTests.BrushEngine_BatchedSegmentsMatchSequentialDryStroke),
        ("Brush engine color mix uses cached tile-major path", BrushTests.BrushEngine_ColorMixUsesCachedTileMajorPath),
        ("Direct draw splits fast long brush segments before rendering", BrushTests.DirectDraw_SplitsFastLongBrushSegments),
        ("Direct draw color mixing samples pre-stroke pixels", BrushTests.DirectDraw_ColorMixingDoesNotSampleOwnStroke),
        ("Brush engine color mixing amount controls deposited color", BrushTests.BrushEngine_ColorMixAmountControlsDepositedColor),
        ("Brush engine does not dispose tip-owned cached masks", BrushTests.BrushEngine_DoesNotDisposeTipOwnedCachedMasks),
        ("Tool controller does not restore stale engine brush state", BrushTests.ToolController_DoesNotOverridePresetBrushState),
        ("Composite tool deactivation keeps completed output intact", BrushTests.CompositeTool_DeactivateDoesNotCancelCompletedOutput),
        ("Composite tool deactivation does not cancel completed output with moves", BrushTests.CompositeTool_DeactivateDoesNotCancelCompletedOutputWithMoves),
        ("Composite tool commit preserves direct draw pixels before temp switch", BrushTests.CompositeTool_CommitPreservesDirectDrawPixelsBeforeTempSwitch),
        ("Composite tool deactivation cancels only active input", BrushTests.CompositeTool_DeactivateCancelsActiveInput),
        ("Image brush tips preserve source aspect ratio", BrushTests.ImageBrushTip_PreservesAspectRatio),
        ("Image brush tips keep cached masks stable across sizes", BrushTests.ImageBrushTip_MasksRemainStableAcrossSizes),
        ("Image brush tips do not dispose old cached masks under cache churn", BrushTests.ImageBrushTip_DoesNotDisposeCachedMasksUnderChurn),
        ("Procedural brush tips do not dispose old cached masks under cache churn", BrushTests.ProceduralBrushTip_DoesNotDisposeCachedMasksUnderChurn),
        ("Procedural brush tips make soft distinct from round", BrushTests.ProceduralBrushTip_SoftRoundDiffersFromRound),
        ("Procedural brush tips make flat rectangular", BrushTests.ProceduralBrushTip_FlatIsRectangular),
        ("Procedural brush tips generate bristle strands", BrushTests.ProceduralBrushTip_BristleHasSeparatedStrands),
        ("Procedural brush tips are graph backed", BrushTests.ProceduralBrushTip_IsGraphBacked),
        ("Procedural brush tip data stores graph payloads", BrushTests.ProceduralBrushTipData_StoresGraphPayload),
        ("Brush tip graphs validate bad topology", BrushTests.BrushTipNodeGraph_ValidatesBadTopology),
        ("Brush tip graph cache keys change with graph content", BrushTests.BrushTipNodeGraph_CacheKeyChangesWithContent),
        ("Node graph ports enforce scalar vs vector compatibility", BrushTests.BrushTipNodePorts_EnforceCompatibility),
        ("Node brush tips evaluate deterministic graphs", BrushTests.NodeBrushTip_EvaluatesDeterministicGraph),
        ("Node brush tips compose procedural primitives", BrushTests.NodeBrushTip_ComposesProceduralPrimitives),
        ("Node brush tips support coordinate warping", BrushTests.NodeBrushTip_SupportsCoordinateWarping),
        ("Node brush tips sample embedded image tips", BrushTests.NodeBrushTip_SamplesEmbeddedImageTip),
        ("Brush material tips resolve active embedded image tips", BrushTests.BrushMaterialTips_ResolveActiveEmbeddedTip),
        ("Node brush tips evaluate color with procedural output", BrushTests.NodeBrushTip_EvaluateColorWithProceduralOutput),
        ("Image brush tips with color paint as colored stamps", BrushTests.ImageBrushTip_ColorStampPreservesColor),
        ("ABR preset mapping keeps usable brush dynamics", BrushTests.AbrPresetMapping_KeepsDynamics),
        ("ABR mask cleanup inverts dark-on-light stamps", BrushTests.AbrMaskCleanup_InvertsDarkOnLightMasks),
        ("Preset store round-trips tool groups", PresetStoreTests.ToolGroups_RoundTrip),
        ("Default tool groups have categories", PresetStoreTests.ToolGroups_DefaultsHaveCategories),
        ("Tool group sync categorizes brush assets", PresetStoreTests.ToolGroups_SyncCategorizesBrushAssets),
        ("Preset store round-trips brush assets", PresetStoreTests.BrushAssets_RoundTrip),
        ("Preset store round-trips node brush tips", PresetStoreTests.BrushAssets_RoundTripNodeBrushTip),
        ("Brush file format round-trips material tip state", PresetStoreTests.BrushFileFormat_RoundTripsMaterialTipState),
        ("Brush file format round-trips node brush tips", PresetStoreTests.BrushFileFormat_RoundTripsNodeBrushTip),
        ("Preset store round-trips all brush fields via Capture/Apply", PresetStoreTests.BrushPresetOverride_RoundTrip),
        ("Preset store isolates overrides between presets", PresetStoreTests.BrushPresetOverride_Isolation),
        ("Preset store isolates angle overrides between presets", PresetStoreTests.BrushPresetOverride_IsolatesAngle),
        ("Preset store captures angle and tip from Capture/Apply", PresetStoreTests.BrushPresetOverride_PreservesTipAndAngle),
        ("ToolPreset clears all brush overrides", PresetStoreTests.BrushPresetOverride_ClearBrushOverrides),
        ("ToolPreset migrates legacy brush override fields", PresetStoreTests.BrushPresetOverride_MigratesLegacyFields),
        ("Preset packages export sub tools with brush assets", PresetStoreTests.Packages_ExportSubTool),
        ("Preset packages export sub tool groups with brush assets", PresetStoreTests.Packages_ExportSubToolGroup),

        ("DrawingDocument starts with one paintable layer", DrawingDocumentTests.Constructor_SetsInitialLayerState),
        ("DrawingDocument adds, selects, duplicates, and deletes layers", DrawingDocumentTests.LayerManagement_WorksForCommonMutations),
        ("DrawingDocument prevents painting locked and group layers", DrawingDocumentTests.CapabilityFlags_RespectLayerState),
        ("DrawingDocument property changes undo and redo", DrawingDocumentTests.LayerPropertyMutations_UndoRedo),
        ("DrawingDocument reference flag participates in undo and duplicate", DrawingDocumentTests.ReferenceLayerFlag_UndoRedoAndDuplicate),
        ("DrawingDocument selection changes undo and redo", DrawingDocumentTests.SelectionMutations_UndoRedo),
        ("DrawingDocument selection inversion keeps renderable mask geometry", DrawingDocumentTests.SelectionInvert_KeepsRenderableMaskGeometry),
        ("DrawingDocument transform can start on empty selected pixels", DrawingDocumentTests.Transform_StartsOnEmptySelection),
        ("DrawingDocument clear active layer records tile history", DrawingDocumentTests.ClearActiveLayer_UndoRedoRestoresPixels),
        ("DrawingDocument move layer validates invalid targets", DrawingDocumentTests.MoveLayer_ValidatesTargetsAndMoves),
        ("DrawingDocument grouping keeps children and can undo", DrawingDocumentTests.GroupSelectedLayers_CreatesGroupAndUndoRestores),
        ("DrawingDocument history kind marks undo and redo", DrawingDocumentTests.HistoryKind_MarksUndoAndRedo),
        ("DrawingDocument import lifecycle resets state", DrawingDocumentTests.ImportLifecycle_ReplacesDocumentState),
        ("DrawingLayer clones tile state through duplicate", DrawingDocumentTests.DuplicateActiveLayer_CopiesPixels),
        ("DrawingDocument background layer fills white without cross-tile bleed", DrawingDocumentTests.AddBackgroundLayer_FillsWhiteAndTilesStayIndependent),
        ("ToolFactory maps eyedropper sampling options", DrawingDocumentTests.ToolFactory_EyedropperOptions),
        ("Timelapse frame selection samples requested duration", TimelapseTests.SelectFrames_SamplesRequestedDuration),
        ("Timelapse export composition respects portrait and landscape", TimelapseTests.ComposeFrame_RespectsAspectModes),

        ("PSD exporter writes parseable layer records", PsdExporterTests.Export_CanBeReadBack),
        ("PSD exporter aligns layer extra data and channel blocks", PsdExporterTests.Export_WritesValidLayerInfoStructure),
        ("PSD exporter preserves folder hierarchy", PsdExporterTests.Export_PreservesFolderHierarchy),

        ("Floss file format round-trips basic document", FlossFileFormatTests.RoundTrip_BasicDocument),
        ("Floss file format round-trips paper color", FlossFileFormatTests.RoundTrip_PaperColor),
        ("Floss file format round-trips paper layer", FlossFileFormatTests.RoundTrip_PaperLayer),
        ("Floss file format round-trips out-of-bounds tiles", FlossFileFormatTests.RoundTrip_OutOfBoundsTiles),
        ("Floss file format round-trips multiple layers with group", FlossFileFormatTests.RoundTrip_MultipleLayersWithGroup),
        ("Floss file format round-trips empty layer", FlossFileFormatTests.RoundTrip_EmptyLayer)
    ];

    public static int Main()
    {
        var failed = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(ex);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{Tests.Count - failed}/{Tests.Count} tests passed");
        return failed == 0 ? 0 : 1;
    }
}

internal static class AssertEx
{
    public static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
    }

    public static void Near(double expected, double actual, double tolerance = 0.0001, string? message = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var left = expected.ToArray();
        var right = actual.ToArray();
        if (!left.SequenceEqual(right))
            throw new InvalidOperationException(message ?? $"Expected [{string.Join(", ", left)}], got [{string.Join(", ", right)}].");
    }
}

internal static class PresetStoreTests
{
    public static void ToolGroups_RoundTrip()
    {
        var path = TempDatabasePath();
        try
        {
            var store = PresetStore.Open(path);
            var brushPreset = new ToolPreset
            {
                Id = "brush-preset",
                Name = "Portable Ink",
                Engine = ToolPresetEngine.Brush,
                InputProcess = InputProcessType.Brush,
                OutputProcess = OutputProcessType.DirectDraw,
                BrushId = "brush-asset",

                BrushOverride = new BrushPresetOverrideDocument
                {
                    Size = 31,
                    Opacity = 0.72,
                    Flow = 0.44,
                    ColorMix = true,
                    SmudgeMode = SmudgeMode.Smear,
                    AmountOfPaint = 0.33,
                    DensityOfPaint = 0.66
                },
                PresetIcon = Icons.BrushOutline
            };
            var fillPreset = new ToolPreset
            {
                Id = "fill-preset",
                Name = "Reference Fill",
                Engine = ToolPresetEngine.Fill,
                InputProcess = InputProcessType.Click,
                OutputProcess = OutputProcessType.FloodFill,
                FillReference = FillReferenceMode.ReferenceLayers,
                ContiguousFill = false,
                Tolerance = 0.22
            };

            store.SaveToolGroups(
            [
                new ToolGroup
                {
                    Id = "group-brush",
                    Name = "Brush",
                    DefaultEngine = ToolPresetEngine.Brush,
                    Shortcut = new AppKeyBinding(Key.B, KeyModifiers.Control),
                    LastActivePresetId = fillPreset.Id,
                    Presets = [brushPreset, fillPreset],
                    Categories = [new ToolCategory { Name = "Ink", PresetIds = [fillPreset.Id, brushPreset.Id] }]
                }
            ]);

            var groups = store.LoadToolGroups();
            AssertEx.Equal(1, groups.Count);
            AssertEx.Equal("Brush", groups[0].Name);
            AssertEx.Equal("Ctrl+B", groups[0].Shortcut.ToString());
            AssertEx.Equal(fillPreset.Id, groups[0].LastActivePresetId);
            AssertEx.Equal("Portable Ink", groups[0].Presets[0].Name);
            AssertEx.Equal(OutputProcessType.DirectDraw, groups[0].Presets[0].OutputProcess);
            AssertEx.Equal("brush-asset", groups[0].Presets[0].BrushId);
            AssertEx.Near(31, groups[0].Presets[0].BrushOverride!.Size!.Value);
            AssertEx.Equal(SmudgeMode.Smear, groups[0].Presets[0].BrushOverride!.SmudgeMode!.Value);
            AssertEx.Near(0.33, groups[0].Presets[0].BrushOverride!.AmountOfPaint!.Value);
            AssertEx.Near(0.66, groups[0].Presets[0].BrushOverride!.DensityOfPaint!.Value);
            AssertEx.Equal(FillReferenceMode.ReferenceLayers, groups[0].Presets[1].FillReference);
            AssertEx.False(groups[0].Presets[1].ContiguousFill);
            AssertEx.SequenceEqual([fillPreset.Id, brushPreset.Id], groups[0].Categories[0].PresetIds);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    public static void BrushAssets_RoundTrip()
    {
        var path = TempDatabasePath();
        try
        {
            var store = PresetStore.Open(path);
            var tip = new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = TestPngBytes()
            };
            var materialTip = new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = TestPngBytes()
            };
            var preset = new BrushPreset("Loaded ABR Stamp", 77, 0.63, 0.38, 0.17, Color.Parse("#123456"), 24)
            {
                Dynamics = new BrushDynamics
                {
                    Size = CurveOption.PressureSpeed(1.4f, 0.25f),
                    Opacity = CurveOption.Pressure(0.8f),
                    Rotation = CurveOption.Pressure(0.3f)
                },
                Flow = 0.42,
                ColorMix = true,
                ColorLoad = 0.62,
                ColorStretch = 0.73,
                BlurAmount = 0.21,
                SmudgeMode = SmudgeMode.Smear,
                MixingMode = MixingMode.Perceptual,
                AmountOfPaint = 0.84,
                DensityOfPaint = 0.95,
                TipDensity = 0.66,
                TipThickness = 0.42,
                TipDirection = BrushTipDirection.Vertical,
                TipSelectionMode = BrushTipSelectionMode.Random,
                Grain = 0.37,
                Smoothing = 0.48,
                BlendMode = SKBlendMode.Multiply,
                BaseAngleSource = BrushDynamics.AngleSource.DirectionOfLine,
                AngleJitter = 0.19f,
                FlipHorizontal = true,
                FlipVertical = true,
                Tip = tip.CreateTip(),
                Tips = [materialTip],
                Shape = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.5f)
            };
            var asset = new BrushAsset
            {
                Id = "abr-stamp",
                Preset = preset,
                Tip = tip,
                ShapeData = new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Ellipse, AspectRatio = 0.5f }
            };

            store.SaveBrushAsset(asset);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT length(a.asset_json), instr(a.asset_json, 'pngBytes'), count(r.resource_id), sum(length(r.data))
                    FROM brush_assets a
                    LEFT JOIN brush_resources r ON r.asset_id = a.id
                    WHERE a.id = 'abr-stamp'
                    GROUP BY a.id
                    """;
                using var reader = command.ExecuteReader();
                AssertEx.True(reader.Read());
                AssertEx.True(reader.GetInt32(0) > 0, "Brush asset JSON should still store preset parameters.");
                AssertEx.Equal(0, reader.GetInt32(1), "Brush asset JSON should not contain PNG byte payload fields.");
                AssertEx.Equal(2, reader.GetInt32(2), "Embedded brush tips should be stored as resource BLOBs.");
                AssertEx.Equal(tip.PngBytes.Length + materialTip.PngBytes.Length, reader.GetInt32(3));
            }

            var loaded = store.LoadBrushAssets().Single();
            AssertEx.Equal("abr-stamp", loaded.Id);
            AssertEx.Equal("", loaded.FilePath);
            AssertEx.Equal(BrushTipStorageKind.EmbeddedPng, loaded.Tip.Kind);
            AssertEx.Equal(tip.PngBytes.Length, loaded.Tip.PngBytes.Length);
            AssertEx.Equal(BrushTipShape.Ellipse, loaded.ShapeData!.Shape);
            AssertEx.Near(77, loaded.Preset.Size);
            AssertEx.Near(0.42, loaded.Preset.Flow);
            AssertEx.Near(0.73, loaded.Preset.ColorStretch);
            AssertEx.Near(0.66, loaded.Preset.TipDensity);
            AssertEx.Near(0.42, loaded.Preset.TipThickness);
            AssertEx.Equal(BrushTipDirection.Vertical, loaded.Preset.TipDirection);
            AssertEx.Equal(BrushTipSelectionMode.Random, loaded.Preset.TipSelectionMode);
            AssertEx.Equal(SmudgeMode.Smear, loaded.Preset.SmudgeMode);
            AssertEx.Equal(MixingMode.Perceptual, loaded.Preset.MixingMode);
            AssertEx.Equal(SKBlendMode.Multiply, loaded.Preset.BlendMode);
            AssertEx.Equal(BrushDynamics.AngleSource.DirectionOfLine, loaded.Preset.BaseAngleSource);
            AssertEx.Near(0.19, loaded.Preset.AngleJitter);
            AssertEx.True(loaded.Preset.FlipHorizontal);
            AssertEx.True(loaded.Preset.FlipVertical);
            AssertEx.Equal(1, loaded.Preset.Tips.Count);
            AssertEx.Equal(materialTip.PngBytes.Length, loaded.Preset.Tips[0].PngBytes.Length);
            AssertEx.True(loaded.Preset.Tip is NodeBrushTip nodeTip && nodeTip.IsDirectImageSampler,
                "Persisted PNG tips should restore as graph-backed image sampler tips.");
            AssertEx.True(loaded.Preset.Shape is { Shape: BrushTipShape.Ellipse, AspectRatio: 0.5f });
            AssertEx.True(loaded.Preset.Dynamics.Size.IsEnabled);
            AssertEx.True(loaded.Preset.Dynamics.Rotation.IsEnabled);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    public static void BrushAssets_RoundTripNodeBrushTip()
    {
        var path = TempDatabasePath();
        try
        {
            var store = PresetStore.Open(path);
            var tip = new BrushTipData
            {
                Kind = BrushTipStorageKind.NodeGraph,
                NodeGraph = BrushTipNodeGraph.BristleRound()
            };
            var asset = new BrushAsset
            {
                Id = "node-tip-brush",
                Tip = tip,
                Preset = new BrushPreset("Node Tip", 32, 1, 0.8, 0.1, Color.Parse("#222222"), 0)
                {
                    Tip = tip.CreateTip(),
                    ParameterGraphs =
                    [
                        BrushParameterGraph.FromDynamics(BrushParameterTarget.Size,
                            CurveOption.Pressure(1f, min: 0.25f, max: 1f))
                    ]
                }
            };

            store.SaveBrushAsset(asset);
            var loaded = store.LoadBrushAssets().Single();

            AssertEx.Equal(BrushTipStorageKind.NodeGraph, loaded.Tip.Kind);
            AssertEx.True(loaded.Tip.NodeGraph != null);
            AssertEx.True(loaded.Preset.Tip is NodeBrushTip);
            AssertEx.Equal("output", loaded.Tip.NodeGraph!.OutputNodeId);
            AssertEx.Equal(4, loaded.Tip.NodeGraph.Nodes.Count);
            AssertEx.Equal(1, loaded.Preset.ParameterGraphs.Count);
            AssertEx.Equal(BrushParameterTarget.Size, loaded.Preset.ParameterGraphs[0].Target);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    public static void ToolGroups_SyncCategorizesBrushAssets()
    {
        var config = new ToolGroupConfig
        {
            Groups =
            [
                new ToolGroup { Id = "brush", Name = "Brush", DefaultEngine = ToolPresetEngine.Brush },
                new ToolGroup { Id = "eraser", Name = "Eraser", DefaultEngine = ToolPresetEngine.Eraser }
            ]
        };
        var pen    = BrushAsset.FromPreset(new BrushPreset("Technical Pen", 8, 1, 0.9, 0.1, Color.Parse("#000000"), 0), category: "Pens");
        var marker = BrushAsset.FromPreset(new BrushPreset("Marker", 32, 1, 0.5, 0.1, Color.Parse("#000000"), 0), category: "Markers");
        var eraser = BrushAsset.FromPreset(new BrushPreset("Eraser", 32, 1, 0.5, 0.1, Color.Parse("#000000"), 0)
        {
            BlendMode = SkiaSharp.SKBlendMode.DstOut
        }, category: "Erasers");

        config.SyncWithAssets([pen, marker, eraser]);

        var brushGroup = config.Groups[0];
        var eraserGroup = config.Groups[1];
        AssertEx.True(brushGroup.Categories.Any(c => c.Name == "Pens" && c.PresetIds.Count == 1));
        AssertEx.True(brushGroup.Categories.Any(c => c.Name == "Markers" && c.PresetIds.Count == 1));
        AssertEx.True(eraserGroup.Categories.Any(c => c.Name == "Erasers" && c.PresetIds.Count == 1));

        var penPreset = brushGroup.Presets.First(p => p.BrushId == pen.Id);
        brushGroup.Categories.Clear();
        config.SyncWithAssets([pen]);
        AssertEx.True(brushGroup.Categories.Any(c => c.Name == "Pens" && c.PresetIds.Contains(penPreset.Id)));
    }

    public static void ToolGroups_DefaultsHaveCategories()
    {
        var config = new ToolGroupConfig();
        foreach (var group in config.Groups)
        {
            AssertEx.True(group.Categories.Count > 0, $"{group.Name} should have a default category.");
            foreach (var preset in group.Presets)
            {
                AssertEx.True(group.Categories.Any(c => c.PresetIds.Contains(preset.Id)),
                    $"{group.Name}/{preset.Name} should appear in a category.");
            }
        }

        var fill = config.Groups.First(g => g.Name == "Fill");
        AssertEx.True(fill.Categories.Any(c => c.Name == "Fill" && c.PresetIds.Count == fill.Presets.Count));

        var select = config.Groups.First(g => g.Name == "Select");
        AssertEx.True(select.Categories.Any(c => c.Name == "Select" && c.PresetIds.Count == select.Presets.Count));
    }

    public static void Packages_ExportSubTool()
    {
        var path = TempPackagePath(PresetPackageFormat.SubToolExtension);
        try
        {
            var asset = TestBrushAsset("asset-one", "Package Brush");
            var preset = new ToolPreset
            {
                Id = "preset-one",
                Name = "Package Brush",
                Engine = ToolPresetEngine.Brush,
                InputProcess = InputProcessType.Brush,
                OutputProcess = OutputProcessType.DirectDraw,
                BrushId = asset.Id,
                BrushOverride = new BrushPresetOverrideDocument { Size = 42 }
            };
            var group = new ToolGroup
            {
                Id = "group-one",
                Name = "Brush",
                DefaultEngine = ToolPresetEngine.Brush,
                LastActivePresetId = preset.Id,
                Presets = [preset, new ToolPreset { Id = "other", Name = "Other", InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.FloodFill }],
                Categories = [new ToolCategory { Name = "Favorites", PresetIds = [preset.Id, "other"] }]
            };

            PresetPackageFormat.ExportSubTool(path, group, preset, [asset]);
            AssertEx.True(new FileInfo(path).Length > 4096, "Package data must be written into the exported file, not only a WAL sidecar.");
            AssertEx.False(File.Exists(path + "-wal"), "Exported sub-tool package must be a single portable file.");
            AssertEx.False(File.Exists(path + "-shm"), "Exported sub-tool package must be a single portable file.");

            var store = PresetStore.Open(path);
            var groups = store.LoadToolGroups();
            var assets = store.LoadBrushAssets();
            AssertEx.Equal(1, groups.Count);
            AssertEx.Equal(1, groups[0].Presets.Count);
            AssertEx.Equal("preset-one", groups[0].Presets[0].Id);
            AssertEx.Equal("asset-one", groups[0].Presets[0].BrushId);
            AssertEx.Equal(1, groups[0].Categories.Count);
            AssertEx.SequenceEqual([preset.Id], groups[0].Categories[0].PresetIds);
            AssertEx.Equal(1, assets.Count);
            AssertEx.Equal("asset-one", assets[0].Id);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    public static void Packages_ExportSubToolGroup()
    {
        var path = TempPackagePath(PresetPackageFormat.SubToolGroupExtension);
        try
        {
            var assetOne = TestBrushAsset("asset-one", "Brush One");
            var assetTwo = TestBrushAsset("asset-two", "Brush Two");
            var group = new ToolGroup
            {
                Id = "group-one",
                Name = "Brushes",
                DefaultEngine = ToolPresetEngine.Brush,
                Presets =
                [
                    new ToolPreset { Id = "preset-one", Name = "One", InputProcess = InputProcessType.Brush, OutputProcess = OutputProcessType.DirectDraw, BrushId = assetOne.Id },
                    new ToolPreset { Id = "preset-two", Name = "Two", InputProcess = InputProcessType.Brush, OutputProcess = OutputProcessType.DirectDraw, BrushId = assetTwo.Id },
                    new ToolPreset { Id = "fill", Name = "Fill", InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.FloodFill }
                ],
                Categories = [new ToolCategory { Name = "All", PresetIds = ["preset-one", "preset-two", "fill"] }]
            };

            PresetPackageFormat.ExportSubToolGroup(path, group, group.Categories[0], [assetOne, assetTwo]);
            AssertEx.True(new FileInfo(path).Length > 4096, "Package data must be written into the exported file, not only a WAL sidecar.");
            AssertEx.False(File.Exists(path + "-wal"), "Exported sub-tool group package must be a single portable file.");
            AssertEx.False(File.Exists(path + "-shm"), "Exported sub-tool group package must be a single portable file.");

            var store = PresetStore.Open(path);
            var groups = store.LoadToolGroups();
            var assets = store.LoadBrushAssets();
            AssertEx.Equal(1, groups.Count);
            AssertEx.Equal(3, groups[0].Presets.Count);
            AssertEx.SequenceEqual(["preset-one", "preset-two", "fill"], groups[0].Categories[0].PresetIds);
            AssertEx.SequenceEqual(["asset-one", "asset-two"], assets.Select(a => a.Id));
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    public static void BrushFileFormat_RoundTripsMaterialTipState()
    {
        var path = TempPackagePath(BrushFileFormat.Extension);
        try
        {
            var tip = new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = TestPngBytes()
            };
            var material = new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = TestPngBytes()
            };
            var asset = new BrushAsset
            {
                Id = "material-tip-brush",
                Tip = tip,
                Preset = new BrushPreset("Material Tip", 42, 0.8, 0.6, 0.12, Color.Parse("#101010"), 18)
                {
                    Tip = tip.CreateTip(),
                    TipDensity = 0.57,
                    TipThickness = 0.33,
                    TipDirection = BrushTipDirection.Vertical,
                    TipSelectionMode = BrushTipSelectionMode.Sequential,
                    FlipHorizontal = true,
                    FlipVertical = true,
                    Tips = [material]
                }
            };

            BrushFileFormat.Save(path, asset);
            var loaded = BrushFileFormat.Load(path);

            AssertEx.True(loaded.Preset.Tip is NodeBrushTip nodeTip && nodeTip.IsDirectImageSampler,
                "Material PNG tips should restore as graph-backed image sampler tips.");
            AssertEx.Near(0.57, loaded.Preset.TipDensity);
            AssertEx.Near(0.33, loaded.Preset.TipThickness);
            AssertEx.Equal(BrushTipDirection.Vertical, loaded.Preset.TipDirection);
            AssertEx.Equal(BrushTipSelectionMode.Sequential, loaded.Preset.TipSelectionMode);
            AssertEx.True(loaded.Preset.FlipHorizontal);
            AssertEx.True(loaded.Preset.FlipVertical);
            AssertEx.Equal(1, loaded.Preset.Tips.Count);
            AssertEx.Equal(material.PngBytes.Length, loaded.Preset.Tips[0].PngBytes.Length);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    public static void BrushFileFormat_RoundTripsNodeBrushTip()
    {
        var path = TempPackagePath(BrushFileFormat.Extension);
        try
        {
            var tip = new BrushTipData
            {
                Kind = BrushTipStorageKind.NodeGraph,
                NodeGraph = BrushTipNodeGraph.BristleRound()
            };
            var asset = new BrushAsset
            {
                Id = "node-file-brush",
                Tip = tip,
                Preset = new BrushPreset("Node File Tip", 41, 0.9, 0.7, 0.08, Color.Parse("#202020"), 0)
                {
                    Tip = tip.CreateTip(),
                    Tips = [tip.DeepClone()],
                    ParameterGraphs =
                    [
                        BrushParameterGraph.FromDynamics(BrushParameterTarget.Opacity,
                            CurveOption.Pressure(1f, min: 0.1f, max: 0.9f))
                    ]
                }
            };

            BrushFileFormat.Save(path, asset);
            var loaded = BrushFileFormat.Load(path);

            AssertEx.Equal(BrushTipStorageKind.NodeGraph, loaded.Tip.Kind);
            AssertEx.True(loaded.Tip.NodeGraph != null);
            AssertEx.True(loaded.Preset.Tip is NodeBrushTip);
            AssertEx.Equal(1, loaded.Preset.Tips.Count);
            AssertEx.Equal(BrushTipStorageKind.NodeGraph, loaded.Preset.Tips[0].Kind);
            AssertEx.Equal(loaded.Tip.NodeGraph!.CacheKey(), loaded.Preset.Tips[0].NodeGraph!.CacheKey());
            AssertEx.Equal(1, loaded.Preset.ParameterGraphs.Count);
            AssertEx.Equal(BrushParameterTarget.Opacity, loaded.Preset.ParameterGraphs[0].Target);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    private static string TempDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"floss-presets-{Guid.NewGuid():N}.flbr");

    private static string TempPackagePath(string extension)
        => Path.Combine(Path.GetTempPath(), $"floss-package-{Guid.NewGuid():N}{extension}");

    private static BrushAsset TestBrushAsset(string id, string name)
    {
        var tip = new BrushTipData
        {
            Kind = BrushTipStorageKind.EmbeddedPng,
            PngBytes = TestPngBytes()
        };
        return new BrushAsset
        {
            Id = id,
            Preset = new BrushPreset(name, 24, 1, 0.8, 0.1, Color.Parse("#000000"), 0)
            {
                Tip = tip.CreateTip()
            },
            Tip = tip
        };
    }

    private static void TryDeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            catch { }
        }
    }

    private static byte[] TestPngBytes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(1, 0, SKColors.White);
        bitmap.SetPixel(1, 1, SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── ToolPreset override round-trip ──────────────────────────────────────

    public static void BrushPresetOverride_RoundTrip()
    {
        var original = new BrushPreset("Round", 42, 0.85, 0.67, 0.11, Color.Parse("#ff0000"), 33)
        {
            Dynamics = new BrushDynamics
            {
                Size = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(0.5f),
                Rotation = CurveOption.PressureSpeed(0.3f, 0.1f)
            },
            Flow = 0.77,
            ColorMix = true,
            ColorLoad = 0.33,
            ColorStretch = 0.44,
            BlurAmount = 0.12,
            SmudgeMode = SmudgeMode.Smear,
            MixingMode = MixingMode.Perceptual,
            AmountOfPaint = 0.55,
            DensityOfPaint = 0.66,
            TipDensity = 0.88,
            TipThickness = 2.5,
            TipDirection = BrushTipDirection.Vertical,
            Grain = 0.22,
            Smoothing = 0.44,
            BlendMode = SKBlendMode.Multiply,
            BaseAngleSource = BrushDynamics.AngleSource.DirectionOfLine,
            AngleJitter = 0.15f,
            Tip = new ImageBrushTip(TestPngBytes()),
            Shape = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.5f)
        };

        var preset = new ToolPreset();
        preset.CaptureFromBrushPreset(original);
        var restored = preset.ApplyToBrushPreset(new BrushPreset("Base", 1, 1, 1, 1, Color.Parse("#000000"), 0));

        AssertEx.Equal("Base", restored.Name, "Name should come from base preset, not captured");
        AssertEx.Near(42, restored.Size);
        AssertEx.Near(0.85, restored.Opacity);
        AssertEx.Near(0.67, restored.Hardness);
        AssertEx.Near(0.11, restored.Spacing);
        AssertEx.Near(0.77, restored.Flow);
        AssertEx.Near(0.22, restored.Grain);
        AssertEx.Near(0.44, restored.Smoothing);
        AssertEx.True(restored.ColorMix);
        AssertEx.Near(0.33, restored.ColorLoad);
        AssertEx.Near(0.44, restored.ColorStretch);
        AssertEx.Near(0.12, restored.BlurAmount);
        AssertEx.Equal(SmudgeMode.Smear, restored.SmudgeMode);
        AssertEx.Equal(MixingMode.Perceptual, restored.MixingMode);
        AssertEx.Near(0.55, restored.AmountOfPaint);
        AssertEx.Near(0.66, restored.DensityOfPaint);
        AssertEx.Near(0.88, restored.TipDensity);
        AssertEx.Near(2.5, restored.TipThickness);
        AssertEx.Equal(BrushTipDirection.Vertical, restored.TipDirection);
        AssertEx.Equal(SKBlendMode.Multiply, restored.BlendMode);

        // Dynamics should be captured and restored
        AssertEx.True(restored.Dynamics.Size.IsEnabled);
        AssertEx.True(restored.Dynamics.Opacity.IsEnabled);
        AssertEx.True(restored.Dynamics.Rotation.IsEnabled);

        // Tool identity and paint color stay on the base preset/default.
        AssertEx.Near(33, restored.Angle, 0.0001, "Angle should be captured by ToolPreset");
        AssertEx.Equal(BrushDynamics.AngleSource.DirectionOfLine, restored.BaseAngleSource, "BaseAngleSource should be captured");
        AssertEx.Near(0.15, restored.AngleJitter, 0.0001, "AngleJitter should be captured");
        AssertEx.Equal(Color.Parse("#000000"), restored.Color, "Color should NOT be captured");

        // Tip/shape are runtime brush state now so graph and image sampler edits survive restart without overwriting the asset default.
        AssertEx.True(restored.Tip is NodeBrushTip nodeTip && nodeTip.IsDirectImageSampler,
            "Captured image tips should restore through graph-backed image samplers.");
        AssertEx.True(restored.Shape is { Shape: BrushTipShape.Ellipse, AspectRatio: 0.5f });
    }

    public static void BrushPresetOverride_Isolation()
    {
        var basePreset = new BrushPreset("Base", 5, 1, 1, 0.5, Color.Parse("#000000"), 0);

        var presetA = new ToolPreset();
        var brushA = basePreset with { Size = 10, Opacity = 0.5 };
        presetA.CaptureFromBrushPreset(brushA);

        var presetB = new ToolPreset();
        var brushB = basePreset with { Size = 20, Opacity = 0.8 };
        presetB.CaptureFromBrushPreset(brushB);

        var restoredA = presetA.ApplyToBrushPreset(basePreset);
        var restoredB = presetB.ApplyToBrushPreset(basePreset);

        AssertEx.Near(10, restoredA.Size, 0.0001, "Preset A should keep its own size");
        AssertEx.Near(0.5, restoredA.Opacity, 0.0001, "Preset A should keep its own opacity");
        AssertEx.Near(20, restoredB.Size, 0.0001, "Preset B should keep its own size");
        AssertEx.Near(0.8, restoredB.Opacity, 0.0001, "Preset B should keep its own opacity");
    }

    public static void BrushPresetOverride_IsolatesAngle()
    {
        var basePreset = new BrushPreset("Base", 5, 1, 1, 0.5, Color.Parse("#000000"), 0);

        var presetA = new ToolPreset();
        presetA.CaptureFromBrushPreset(basePreset with
        {
            Angle = 30,
            BaseAngleSource = BrushDynamics.AngleSource.DirectionOfLine,
            AngleJitter = 0.1f
        });

        var presetB = new ToolPreset();
        presetB.CaptureFromBrushPreset(basePreset with
        {
            Angle = 120,
            BaseAngleSource = BrushDynamics.AngleSource.PenTilt,
            AngleJitter = 0.4f
        });

        var restoredA = presetA.ApplyToBrushPreset(basePreset);
        var restoredB = presetB.ApplyToBrushPreset(basePreset);

        AssertEx.Near(30, restoredA.Angle, 0.0001, "Preset A should keep its own angle");
        AssertEx.Equal(BrushDynamics.AngleSource.DirectionOfLine, restoredA.BaseAngleSource);
        AssertEx.Near(0.1, restoredA.AngleJitter, 0.0001);
        AssertEx.Near(120, restoredB.Angle, 0.0001, "Preset B should keep its own angle");
        AssertEx.Equal(BrushDynamics.AngleSource.PenTilt, restoredB.BaseAngleSource);
        AssertEx.Near(0.4, restoredB.AngleJitter, 0.0001);
    }

    public static void BrushPresetOverride_PreservesTipAndAngle()
    {
        var fullPreset = new BrushPreset("Custom", 15, 0.9, 0.6, 0.2, Color.Parse("#112233"), 45)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle, 1.0f),
            BaseAngleSource = BrushDynamics.AngleSource.PenTilt,
            AngleJitter = 0.25f,
            Shape = new ProceduralBrushTip(BrushTipShape.Rectangle, 1.5f)
        };

        var preset = new ToolPreset();
        preset.CaptureFromBrushPreset(fullPreset);

        // Apply to a DIFFERENT base — the captured fields should override, rest should not
        var differentBase = new BrushPreset("Default", 1, 1, 1, 1, Color.Parse("#000000"), 0);
        var restored = preset.ApplyToBrushPreset(differentBase);

        // Captured fields override the base
        AssertEx.Near(15, restored.Size);
        AssertEx.Near(0.9, restored.Opacity);

        // Identity still comes from the base; captured angle settings override it.
        AssertEx.Equal("Default", restored.Name);
        AssertEx.Near(45, restored.Angle, 0.0001, "Angle should be captured");
        AssertEx.Equal(BrushDynamics.AngleSource.PenTilt, restored.BaseAngleSource);
        AssertEx.Near(0.25, restored.AngleJitter, 0.0001);
        AssertEx.True(restored.Tip is NodeBrushTip, "Tip graph should be captured as runtime brush state");
        AssertEx.True(restored.Shape is { Shape: BrushTipShape.Rectangle, AspectRatio: 1.5f },
            "Shape should be captured as runtime brush state");
    }

    public static void BrushPresetOverride_ClearBrushOverrides()
    {
        var preset = new ToolPreset();
        preset.CaptureFromBrushPreset(new BrushPreset("Custom", 15, 0.9, 0.6, 0.2, Color.Parse("#112233"), 45)
        {
            Dynamics = new BrushDynamics
            {
                Size = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(0.5f)
            },
            Flow = 0.7,
            Grain = 0.4,
            Smoothing = 0.3,
            ColorMix = true,
            ColorLoad = 0.2,
            ColorStretch = 0.8,
            BlurAmount = 0.1,
            SmudgeMode = SmudgeMode.Smear,
            MixingMode = MixingMode.Perceptual,
            AmountOfPaint = 0.55,
            DensityOfPaint = 0.66,
            TipDensity = 0.77,
            TipThickness = 0.88,
            TipDirection = BrushTipDirection.Vertical,
            BlendMode = SKBlendMode.Multiply
        });

        preset.ClearBrushOverrides();

        AssertEx.True(preset.BrushOverride is null);
        AssertEx.True(preset.BrushSize is null);
    }

    public static void BrushPresetOverride_MigratesLegacyFields()
    {
        var preset = new ToolPreset
        {
            BrushSize = 31,
            BrushOpacity = 0.72,
            BrushAngle = 45,
            BrushBaseAngleSource = BrushDynamics.AngleSource.DirectionOfLine,
            BrushAngleJitter = 0.2f,
            BrushShapeOverrideSet = true
        };

        preset.MigrateBrushOverrideFormat();

        AssertEx.Near(31, preset.BrushOverride!.Size!.Value);
        AssertEx.Near(0.72, preset.BrushOverride.Opacity!.Value);
        AssertEx.Near(45, preset.BrushOverride.Angle!.Value);
        AssertEx.Equal(BrushDynamics.AngleSource.DirectionOfLine, preset.BrushOverride.BaseAngleSource!.Value);
        AssertEx.Near(0.2, preset.BrushOverride.AngleJitter!.Value);
        AssertEx.Equal(BrushShapeOverrideMode.Null, preset.BrushOverride.ShapeOverride);
        AssertEx.True(preset.BrushSize is null);
    }

    public static void BrushPresetOverride_CapturesNullShape()
    {
        var graphTip = new NodeBrushTip(BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Chalk));
        var fullPreset = new BrushPreset("Graph", 12, 1, 1, 0.5, Color.Parse("#112233"), 0)
        {
            Tip = graphTip,
            Shape = null
        };

        var preset = new ToolPreset();
        preset.CaptureFromBrushPreset(fullPreset);

        var differentBase = new BrushPreset("Default", 1, 1, 1, 1, Color.Parse("#000000"), 0)
        {
            Shape = new ProceduralBrushTip(BrushTipShape.Rectangle, 1.0f)
        };
        var restored = preset.ApplyToBrushPreset(differentBase);

        AssertEx.Equal(BrushShapeOverrideMode.Null, preset.BrushOverride!.ShapeOverride, "Captured null shape must be an explicit override");
        AssertEx.True(restored.Shape is null, "Graph/image brush state should not inherit the base procedural shape");
    }
}

internal static class PixelRegionTests
{
    public static void Intersect_ReturnsOverlap()
        => AssertEx.Equal(new PixelRegion(5, 6, 5, 2), new PixelRegion(0, 0, 10, 10).Intersect(new PixelRegion(5, 6, 20, 2)));

    public static void Intersect_ReturnsEmptyForTouchingEdges()
        => AssertEx.Equal(PixelRegion.Empty, new PixelRegion(0, 0, 10, 10).Intersect(new PixelRegion(10, 0, 5, 5)));

    public static void Union_HandlesEmptyRegions()
    {
        var region = new PixelRegion(-2, 3, 5, 7);
        AssertEx.Equal(region, PixelRegion.Empty.Union(region));
        AssertEx.Equal(region, region.Union(PixelRegion.Empty));
        AssertEx.Equal(new PixelRegion(-2, -1, 12, 11), region.Union(new PixelRegion(4, -1, 6, 3)));
    }

    public static void Transforms_WorkTogether()
    {
        var region = new PixelRegion(2, 3, 4, 5).Inflate(2).Translate(-1, 1).ClipTo(6, 8);
        AssertEx.Equal(new PixelRegion(0, 2, 6, 6), region);
    }
}

internal static class TiledPixelBufferTests
{
    public static void Constructor_ClampsMinimumSize()
    {
        var pixels = new TiledPixelBuffer(0, -20);
        AssertEx.Equal(1, pixels.Width);
        AssertEx.Equal(1, pixels.Height);
        AssertEx.Equal(new PixelRegion(0, 0, 1, 1), pixels.Bounds);
    }

    public static void SetPixel_ReadsAcrossPositiveAndNegativeTiles()
    {
        var pixels = new TiledPixelBuffer(4, 4);
        pixels.SetPixel(-1, -65, 1, 2, 3, 4);
        pixels.SetPixel(64, 64, 5, 6, 7, 8);

        pixels.GetPixel(-1, -65, out var b1, out var g1, out var r1, out var a1);
        pixels.GetPixel(64, 64, out var b2, out var g2, out var r2, out var a2);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3, 4 }, [b1, g1, r1, a1]);
        AssertEx.SequenceEqual(new byte[] { 5, 6, 7, 8 }, [b2, g2, r2, a2]);
        AssertEx.True(pixels.MinX <= -64 && pixels.MinY <= -128);
        AssertEx.True(pixels.MaxX >= 128 && pixels.MaxY >= 128);
    }

    public static void CaptureAndRestore_RoundTripPartialRegion()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(2, 2, 10, 20, 30, 40);
        pixels.SetPixel(3, 2, 50, 60, 70, 80);
        var capture = pixels.Capture(new PixelRegion(2, 2, 2, 1));

        pixels.Clear();
        pixels.Restore(new PixelRegion(5, 6, 2, 1), capture);

        pixels.GetPixel(5, 6, out var b1, out _, out _, out var a1);
        pixels.GetPixel(6, 6, out var b2, out _, out _, out var a2);
        AssertEx.Equal((byte)10, b1);
        AssertEx.Equal((byte)40, a1);
        AssertEx.Equal((byte)50, b2);
        AssertEx.Equal((byte)80, a2);
    }

    public static void Clear_RemovesOnlyRequestedPixels()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(1, 1, 1, 1, 1, 255);
        pixels.SetPixel(4, 4, 2, 2, 2, 255);
        pixels.Clear(new PixelRegion(0, 0, 2, 2));

        pixels.GetPixel(1, 1, out _, out _, out _, out var clearedAlpha);
        pixels.GetPixel(4, 4, out var remainingBlue, out _, out _, out var remainingAlpha);
        AssertEx.Equal((byte)0, clearedAlpha);
        AssertEx.Equal((byte)2, remainingBlue);
        AssertEx.Equal((byte)255, remainingAlpha);
    }

    public static void Clear_RemovesTransparentTiles()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(10, 10, 1, 2, 3, 255);
        AssertEx.True(pixels.HasContentTiles(new PixelRegion(10, 10, 1, 1)));
        pixels.Clear(new PixelRegion(0, 0, TiledPixelBuffer.TileSize, TiledPixelBuffer.TileSize));
        AssertEx.False(pixels.HasContentTiles(new PixelRegion(10, 10, 1, 1)));
        AssertEx.Equal(PixelRegion.Empty, pixels.ContentTileBounds);
    }

    public static void CopyFromBgra_SkipsTransparentPixels()
    {
        var pixels = new TiledPixelBuffer(4, 4);
        var source = new byte[]
        {
            1, 2, 3, 0,
            4, 5, 6, 255,
            7, 8, 9, 128,
            10, 11, 12, 0
        };
        pixels.CopyFromBgra(new PixelRegion(2, 3, 2, 2), source, 8);

        pixels.GetPixel(2, 3, out _, out _, out _, out var transparentAlpha);
        pixels.GetPixel(3, 3, out var b, out var g, out var r, out var a);
        pixels.GetPixel(2, 4, out var b2, out _, out _, out var a2);
        AssertEx.Equal((byte)0, transparentAlpha);
        AssertEx.SequenceEqual(new byte[] { 4, 5, 6, 255 }, [b, g, r, a]);
        AssertEx.Equal((byte)7, b2);
        AssertEx.Equal((byte)128, a2);
    }

    public static void CaptureTiles_ReturnsDefensiveCopies()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(0, 0, 1, 2, 3, 255);
        var captured = pixels.CaptureTiles();
        captured[(0, 0)][3] = 0;
        pixels.GetPixel(0, 0, out _, out _, out _, out var alpha);
        AssertEx.Equal((byte)255, alpha);
    }

    public static void ComputeContentBounds_ReturnsNonTransparentExtents()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(-2, 5, 1, 1, 1, 255);
        pixels.SetPixel(65, 1, 1, 1, 1, 255);
        AssertEx.Equal(new PixelRegion(-2, 1, 68, 5), pixels.ComputeContentBounds());
    }

    public static void Restore_TruncatedBytesDoesNotThrowOrOverread()
    {
        var pixels = new TiledPixelBuffer(4, 4);
        pixels.Restore(new PixelRegion(0, 0, 2, 2), [9, 8, 7, 255, 6, 5, 4, 255]);
        pixels.GetPixel(0, 0, out var b1, out _, out _, out var a1);
        pixels.GetPixel(1, 0, out var b2, out _, out _, out var a2);
        pixels.GetPixel(0, 1, out _, out _, out _, out var missingAlpha);
        AssertEx.Equal((byte)9, b1);
        AssertEx.Equal((byte)255, a1);
        AssertEx.Equal((byte)6, b2);
        AssertEx.Equal((byte)255, a2);
        AssertEx.Equal((byte)0, missingAlpha);
    }

    public static void FillSolid_SharedTemplatesDoNotLeakMutations()
    {
        var pixels = new TiledPixelBuffer(128, 64);
        pixels.FillSolid(new PixelRegion(0, 0, 128, 64), 10, 20, 30, 255);

        pixels.Clear(new PixelRegion(0, 0, 1, 1));

        pixels.GetPixel(0, 0, out _, out _, out _, out var clearedAlpha);
        pixels.GetPixel(64, 0, out var b, out var g, out var r, out var a);
        AssertEx.Equal((byte)0, clearedAlpha);
        AssertEx.SequenceEqual(new byte[] { 10, 20, 30, 255 }, [b, g, r, a]);
    }

    public static void ScratchDisk_RoundTripsTilesThroughDisk()
    {
        var originalThreshold = TileSwapManager.MemoryThreshold;
        try
        {
            // Set a very low threshold so any compressed tile gets evicted.
            TileSwapManager.MemoryThreshold = 1;

            var pixels = new TiledPixelBuffer(256, 256);
            pixels.SetPixel(0, 0, 11, 22, 33, 255);
            pixels.SetPixel(128, 128, 44, 55, 66, 255);

            // Compress — this should trigger eviction to disk because threshold is 1 byte.
            pixels.CompressTiles();

            // At this point tiles are either in _compressed or on disk.
            // Force a clear of raw tiles so we must read from disk.
            pixels.CompressTiles();

            // Read back — EnsureRaw should fetch from disk.
            pixels.GetPixel(0, 0, out var b1, out var g1, out var r1, out var a1);
            pixels.GetPixel(128, 128, out var b2, out var g2, out var r2, out var a2);

            AssertEx.SequenceEqual(new byte[] { 11, 22, 33, 255 }, [b1, g1, r1, a1]);
            AssertEx.SequenceEqual(new byte[] { 44, 55, 66, 255 }, [b2, g2, r2, a2]);

            pixels.Dispose();
        }
        finally
        {
            TileSwapManager.MemoryThreshold = originalThreshold;
        }
    }
}

internal static class LayerCompositorTests
{
    public static void MonochromeExpression_ThresholdsCoverageBeforePaperComposite()
    {
        using var layer = new DrawingLayer("Ink", 4, 1)
        {
            ExpressionColor = ExpressionColorMode.Monochrome
        };

        layer.Pixels.SetPixel(0, 0, 0, 0, 0, 127);
        layer.Pixels.SetPixel(1, 0, 0, 0, 0, 128);
        layer.Pixels.SetPixel(2, 0, 200, 200, 200, 255);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([layer], 4, 1, 0xFFFFFFFF);

        AssertPixel(pixels, 0, 255, 255, 255, 255);
        AssertPixel(pixels, 1, 0, 0, 0, 255);
        AssertPixel(pixels, 2, 255, 255, 255, 255);
    }

    public static void SampleCompositePixel_UsesFinalCompositorResult()
    {
        using var layer = new DrawingLayer("Multiply red", 1, 1)
        {
            BlendMode = "Multiply"
        };
        layer.Pixels.SetPixel(0, 0, b: 0, g: 0, r: 255, a: 255);

        using var compositor = new LayerCompositor();
        var sampled = compositor.SampleCompositePixel([layer], 1, 1, 0, 0, paperColor: 0xFF0000FF);

        AssertEx.True(sampled.HasValue, "Sampling image mode should return the final composited pixel.");
        AssertEx.Equal((byte)0, sampled!.Value.R);
        AssertEx.Equal((byte)0, sampled.Value.G);
        AssertEx.Equal((byte)0, sampled.Value.B);
        AssertEx.Equal((byte)255, sampled.Value.A);
    }

    public static void Composite_BudgetsDirtyTiles()
    {
        var dirtyTileCount = LayerCompositor.CountTilesForRegion(new PixelRegion(0, 0, 4096, 4096), lod: 0);

        AssertEx.True(dirtyTileCount > LayerCompositor.DirtyTileBudget);
        AssertEx.Equal(32, LayerCompositor.DirtyTileBudget);
    }

    public static void Composite_SelectsLodForHugeLowZoomCanvas()
    {
        using var compositor = new LayerCompositor();
        AssertEx.Equal(2, compositor.SelectLod(6000, 4080, 0.1));
        AssertEx.Equal(1, compositor.SelectLod(6000, 4080, 0.3));
        AssertEx.Equal(0, compositor.SelectLod(6000, 4080, 1.0));
    }

    private static void AssertPixel(byte[] pixels, int x, byte b, byte g, byte r, byte a)
    {
        var offset = x * 4;
        AssertEx.SequenceEqual(new[] { b, g, r, a },
            new[] { pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3] });
    }
}

internal static class KeyBindingTests
{
    public static void Parse_HandlesFriendlyNamesAndModifiers()
    {
        var binding = AppKeyBinding.Parse("Ctrl+Shift+[");
        AssertEx.Equal(Key.OemOpenBrackets, binding.Key);
        AssertEx.Equal(KeyModifiers.Control | KeyModifiers.Shift, binding.Modifiers);
        AssertEx.True(AppKeyBinding.Parse("Alt").IsModifierOnly);
        AssertEx.True(AppKeyBinding.Parse("not-a-key").IsEmpty);
    }

    public static void ToStringAndDisplay_ReturnExpectedText()
    {
        AssertEx.Equal("Ctrl+Del", new AppKeyBinding(Key.Delete, KeyModifiers.Control).ToString());
        AssertEx.Equal("--", AppKeyBinding.Empty.Display());
    }

    public static void Matches_HandlesModifierOnlyBindings()
    {
        AssertEx.True(new AppKeyBinding(Key.None, KeyModifiers.Alt).Matches(Key.A, KeyModifiers.Alt));
        AssertEx.False(new AppKeyBinding(Key.A, KeyModifiers.Alt).Matches(Key.A, KeyModifiers.None));
        AssertEx.False(AppKeyBinding.Empty.Matches(Key.A, KeyModifiers.None));
    }

    public static void ModifierHelpers_UpdateModifierFlags()
    {
        var modifiers = AppKeyBinding.ModifiersWithKeyDown(Key.LeftCtrl, KeyModifiers.Shift);
        AssertEx.Equal(KeyModifiers.Control | KeyModifiers.Shift, modifiers);
        AssertEx.Equal(KeyModifiers.Shift, AppKeyBinding.ModifiersAfterKeyUp(Key.RightCtrl, modifiers));
    }

    public static void JsonConverter_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppKeyBinding(Key.OemComma, KeyModifiers.Control));
        AssertEx.Equal("\"Ctrl\\u002B,\"", json);
        var binding = JsonSerializer.Deserialize<AppKeyBinding>(json);
        AssertEx.Equal(Key.OemComma, binding!.Key);
        AssertEx.Equal(KeyModifiers.Control, binding.Modifiers);
    }
}

internal static class BrushSizeAdjustmentTests
{
    public static void ScalesSmoothlyAcrossSizes()
    {
        AssertEx.Near(400, BrushSizeAdjustment.FromRadiusDistance(200, 1, 2000));
        AssertEx.Near(20, BrushSizeAdjustment.FromRadiusDistance(10, 1, 2000));

        var smallNudge = BrushSizeAdjustment.Nudge(10, 1, 2, 1, 2000);
        var largeBrushNudge = BrushSizeAdjustment.Nudge(1000, 1, 2, 1, 2000);
        AssertEx.Near(12, smallNudge);
        AssertEx.True(largeBrushNudge - 1000 > 2, "Expected large brushes to use proportional nudge deltas.");
    }

    public static void ClampsBoundaries()
    {
        AssertEx.Equal(1.0, BrushSizeAdjustment.FromRadiusDistance(0, 1, 2000));
        AssertEx.Equal(2000.0, BrushSizeAdjustment.FromRadiusDistance(2000, 1, 2000));
        AssertEx.Equal(1.0, BrushSizeAdjustment.Nudge(1, -1, 10, 1, 2000));
        AssertEx.Equal(2000.0, BrushSizeAdjustment.Nudge(2000, 1, 10, 1, 2000));
    }
}

internal static class BrushTests
{
    public static void CubicCurve_EvaluatesIdentityAndLinearCurves()
    {
        var identity = CubicCurve.Identity();
        AssertEx.Near(0.0, identity.Evaluate(-1));
        AssertEx.Near(1.0, identity.Evaluate(2));
        AssertEx.Near(0.498, identity.Evaluate(0.5f), 0.01);

        var flat = CubicCurve.Linear(0, 0.25f, 1, 0.75f);
        AssertEx.Near(0.25, flat.Evaluate(0), 0.001);
        AssertEx.Near(0.75, flat.Evaluate(1), 0.001);
    }

    public static void CubicCurve_PointManagementAndSerialization()
    {
        var curve = new CubicCurve();
        curve.SetPoints([new CurvePoint(1.5f, -1f), new CurvePoint(0.25f, 0.5f), new CurvePoint(0f, 0f)]);
        AssertEx.SequenceEqual(new[] { 0f, 0.25f, 1f }, curve.Points.Select(p => p.X));

        curve.MovePoint(1, 0.75f, 0.75f);
        curve.AddPoint(0.5f, 0.3f);
        curve.RemovePoint(0);
        AssertEx.Equal(3, curve.Points.Count);

        var restored = CubicCurve.Deserialize(curve.Serialize());
        AssertEx.Equal(curve.Points.Count, restored.Points.Count);

        var clone = curve.Clone();
        clone.MovePoint(0, 0, 0);
        AssertEx.False(curve.Points[0].Equals(clone.Points[0]));
        AssertEx.Equal(256, clone.GetLut().Length);
    }

    public static void SensorConfig_RawValuesAreNormalized()
    {
        var point = StrokePoint();
        AssertEx.Near(0.6, new SensorConfig { Type = SensorType.Pressure }.RawValue(point));
        AssertEx.Near(0.75, new SensorConfig { Type = SensorType.Distance, Length = 200 }.RawValue(point));
        AssertEx.Near(0.25, new SensorConfig { Type = SensorType.Fade, Length = 40 }.RawValue(point));
        AssertEx.Near(0.75, new SensorConfig { Type = SensorType.TiltX }.RawValue(point));
        AssertEx.Near(0.25, new SensorConfig { Type = SensorType.TiltY }.RawValue(point));
        AssertEx.Near(0.75, new SensorConfig { Type = SensorType.Rotation }.RawValue(point));
    }

    public static void CurveOption_ComputesAndClones()
    {
        var point = StrokePoint(pressure: 0.5f, speed: 0.25f);
        var option = new CurveOption
        {
            MinOutput = 0.2f,
            MaxOutput = 0.8f,
            Strength = 0.5f,
            CombineMode = SensorCombineMode.Add,
            Sensors =
            [
                new SensorConfig { Type = SensorType.Pressure, Curve = CubicCurve.Identity() },
                new SensorConfig { Type = SensorType.Speed, Curve = CubicCurve.Identity() }
            ]
        };

        AssertEx.Near(0.7125, option.Compute(point), 0.01);
        AssertEx.Equal(1.0f, CurveOption.Off().Compute(point));

        var clone = option.Clone();
        clone.Sensors[0].Type = SensorType.Random;
        AssertEx.Equal(SensorType.Pressure, option.Sensors[0].Type);
    }

    public static void BrushDynamics_SerializesAndFallsBack()
    {
        var dynamics = new BrushDynamics
        {
            Size = CurveOption.Pressure(1.0f, 0.1f, 0.9f),
            Rotation = new CurveOption
            {
                Sensors = [new SensorConfig { Type = SensorType.Random, Curve = CubicCurve.Identity() }]
            },
            TipDensity = CurveOption.Pressure(1.0f),
            TipThickness = CurveOption.Pressure(1.0f)
        };
        var restored = BrushDynamics.Deserialize(dynamics.Serialize());
        AssertEx.Near(dynamics.EvalSize(StrokePoint(pressure: 0.5f)), restored.EvalSize(StrokePoint(pressure: 0.5f)), 0.01);
        AssertEx.Near(72.0, restored.EvalRotationDeg(StrokePoint(random: 0.7f)), 2.0);
        AssertEx.Near(0.5, restored.EvalTipDensity(StrokePoint(pressure: 0.5f)), 0.02);
        AssertEx.Near(0.5, restored.EvalTipThickness(StrokePoint(pressure: 0.5f)), 0.02);
        AssertEx.Equal(1.0f, BrushDynamics.Deserialize("{bad json").EvalSize(StrokePoint()));
    }

    public static void BrushParameterGraph_EvaluatesStrokeInputs()
    {
        var graph = new BrushParameterGraph
        {
            Target = BrushParameterTarget.Size,
            OutputNodeId = "output",
            Nodes =
            [
                new BrushParameterNode { Id = "pressure", Kind = BrushParameterNodeKind.Pressure, Min = 0.2f, Max = 1.0f, Strength = 1f },
                new BrushParameterNode { Id = "speed", Kind = BrushParameterNodeKind.Velocity, Min = 1.0f, Max = 0.5f, Strength = 1f },
                new BrushParameterNode { Id = "mul", Kind = BrushParameterNodeKind.Multiply, Inputs = ["pressure", "speed"] },
                new BrushParameterNode { Id = "output", Kind = BrushParameterNodeKind.Output, Inputs = ["mul"] }
            ]
        };
        var slow = StrokePoint(pressure: 1f, speed: 0f);
        var fast = StrokePoint(pressure: 1f, speed: 1f);

        AssertEx.True(graph.Validate().Count == 0);
        AssertEx.Near(1.0, graph.Evaluate(slow), 0.001);
        AssertEx.Near(0.5, graph.Evaluate(fast), 0.001);
    }

    public static void BrushPreset_LegacyDynamicsBridge()
    {
        var preset = new BrushPreset("Test", 10, 1, 1, 1, Colors.Black, 0)
        {
            SizeDynamics = ParameterDynamics.DefaultSize,
            OpacityDynamics = ParameterDynamics.DefaultOpacity
        };

        AssertEx.True(preset.SizeDynamics.PressureEnabled);
        AssertEx.True(preset.SizeDynamics.VelocityEnabled);
        AssertEx.True(preset.OpacityDynamics.PressureEnabled);
        AssertEx.False(preset.OpacityDynamics.VelocityEnabled);
    }

    public static void BrushEngine_ReusesMasksDuringLargeStrokes()
    {
        using var engine = new BrushEngine();
        var tip = new CountingBrushTip();
        var brush = new BrushPreset("Big", 160, 1, 0.7, 0.04, Colors.Black, 0)
        {
            Tip = tip,
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 1200, 320);
        var from = Sample(40, 160, 0);
        var to = Sample(1050, 160, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal(1, tip.GenerateCount, "Large strokes should reuse the stroke mask instead of regenerating it per stamp.");
    }

    public static void BrushEngine_ColorMixOffIgnoresWetPaintFields()
    {
        using var engine = new BrushEngine();
        var brush = new BrushPreset("Dry", 30, 1, 1, 0.1, Colors.Black, 0)
        {
            ColorMix = false,
            DensityOfPaint = 0.0,
            AmountOfPaint = 0.0,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 120, 120);
        var sample = Sample(60, 60, 0);

        var dirty = engine.RasterizeDab(layer, brush, sample, velocity: 0);

        AssertEx.False(dirty.IsEmpty);
        layer.Pixels.GetPixel(60, 60, out _, out _, out _, out var alpha);
        AssertEx.True(alpha > 0, "Disabling color mix should make wet-paint fields inert, not make the brush invisible.");
    }

    public static void BrushEngine_AppliesTipThickness()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 100, 100);
        var brush = new BrushPreset("Flat tip", 40, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            TipThickness = 0.25,
            TipDirection = BrushTipDirection.Horizontal,
            Shape = null
        };
        var sample = Sample(50, 50, 0);

        engine.BeginStroke(brush, sample);
        engine.RasterizeDab(layer, brush, sample, velocity: 0);

        var (minX, minY, maxX, maxY) = AlphaBounds(layer);
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        AssertEx.True(width > height * 2, $"Horizontal tip thickness should flatten the rendered stamp, got {width}x{height}.");
    }

    public static void BrushEngine_AppliesTipDensityAndThicknessDynamics()
    {
        using var lowEngine = new BrushEngine();
        using var highEngine = new BrushEngine();
        var lowLayer = new DrawingLayer("Low", 100, 100);
        var highLayer = new DrawingLayer("High", 100, 100);
        var brush = new BrushPreset("Dynamic tip", 40, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            TipDensity = 1,
            TipThickness = 1,
            TipDirection = BrushTipDirection.Horizontal,
            Dynamics = new BrushDynamics
            {
                TipDensity = CurveOption.Pressure(1.0f),
                TipThickness = CurveOption.Pressure(1.0f)
            },
            Shape = null
        };

        lowEngine.RasterizeDab(lowLayer, brush, SampleWithPressure(50, 50, 0, 0.25), velocity: 0);
        highEngine.RasterizeDab(highLayer, brush, SampleWithPressure(50, 50, 0, 1.0), velocity: 0);

        lowLayer.Pixels.GetPixel(50, 50, out _, out _, out _, out var lowAlpha);
        highLayer.Pixels.GetPixel(50, 50, out _, out _, out _, out var highAlpha);
        var (_, lowMinY, _, lowMaxY) = AlphaBounds(lowLayer);
        var (_, highMinY, _, highMaxY) = AlphaBounds(highLayer);

        AssertEx.True(highAlpha > lowAlpha, "Tip density pressure dynamics should affect rendered stamp opacity.");
        AssertEx.True(highMaxY - highMinY > lowMaxY - lowMinY, "Tip thickness pressure dynamics should affect rendered stamp thickness.");
    }

    public static void BrushEngine_BlendModeDoesNotCarryPaint()
    {
        using var engine = new BrushEngine();
        var brush = new BrushPreset("Blend", 8, 1, 1, 0.6, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0,
            DensityOfPaint = 0,
            ColorStretch = 0.1,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 180, 60);
        for (var y = 26; y <= 34; y++)
        for (var x = 16; x <= 24; x++)
            layer.Pixels.SetPixel(x, y, 0, 0, 0, 255);

        var from = Sample(20, 30, 0);
        var to = Sample(150, 30, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        layer.Pixels.GetPixel(140, 30, out _, out _, out _, out var farAlpha);
        AssertEx.Equal((byte)0, farAlpha, "Blend mode should blur local paint, not carry it across transparent canvas.");
    }

    public static void BrushEngine_LowSpacingUsesCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Low spacing", 48, 1, 0.75, 0.01, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal("CachedTileMajor", engine.LastStats.Path);
        AssertEx.True(engine.LastStats.StampCount > 10, $"Expected low spacing to generate many dabs, got {engine.LastStats.StampCount}.");
        AssertEx.Equal(engine.LastStats.StampCount, engine.LastStats.CachedDabCount);
        AssertEx.True(engine.LastStats.TileBucketCount > 0);
    }

    public static void BrushEngine_ShapedTipsUseCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Shaped", 44, 1, 0.8, 0.02, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Chalk),
            Shape = new ProceduralBrushTip(BrushTipShape.Rectangle),
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal("CachedTileMajor", engine.LastStats.Path);
        AssertEx.True(engine.LastStats.CachedDabCount > 10);
        AssertEx.True(engine.LastStats.TileBucketCount > 0);
    }

    public static void BrushEngine_MultiTipsUseCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Multi-tip", 42, 1, 0.85, 0.02, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Tips =
            [
                new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Circle },
                new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Chalk }
            ],
            TipSelectionMode = BrushTipSelectionMode.Sequential,
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal("CachedTileMajor", engine.LastStats.Path);
        AssertEx.True(engine.LastStats.CachedDabCount > 10);
        AssertEx.True(engine.LastStats.TileBucketCount > 0);
    }

    public static void BrushEngine_SingleModeIgnoresMaterialTipList()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 96, 96);
        var activeTip = new CountingBrushTip();
        var brush = new BrushPreset("Procedural active with saved materials", 32, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = activeTip,
            Tips = [new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Flat }],
            TipSelectionMode = BrushTipSelectionMode.Single,
            Shape = null,
            Dynamics = new BrushDynamics()
        };
        var sample = Sample(48, 48, 0);

        engine.BeginStroke(brush, sample);
        var dirty = engine.RasterizeDab(layer, brush, sample, velocity: 0);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.True(activeTip.GenerateCount > 0, "Single tip mode must render brush.Tip, not the saved material tip list.");
    }

    public static void BrushEngine_ColorImageTipsUseCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Color material", 36, 1, 1, 0.02, Colors.Black, 0)
        {
            Tip = new ImageBrushTip(ColoredTipPngBytes(SKColors.Red)),
            Shape = null,
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal("CachedColorTileMajor", engine.LastStats.Path);
        AssertEx.True(engine.LastStats.CachedDabCount > 10);
        layer.Pixels.GetPixel(240, 128, out var b, out var g, out var r, out var a);
        AssertEx.True(a > 0, "Colored image tip should deposit alpha.");
        AssertEx.True(r > 180 && g < 80 && b < 80, $"Colored image tip should preserve red pixels, got BGRA=({b},{g},{r},{a}).");
    }

    public static void BrushEngine_DabCacheSurvivesBatchedUniqueStamps()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 1024, 512);
        var brush = new BrushPreset("Cache stress", 48, 1, 1, 0.008, Colors.Black, 0)
        {
            Tip = new ImageBrushTip(ColoredTipPngBytes(SKColors.Red)),
            Shape = null,
            ColorMix = false,
            Grain = 0,
            SizeDynamics = new ParameterDynamics { PressureEnabled = true },
            Dynamics = new BrushDynamics()
        };

        var samples = new List<CanvasInputSample>(130);
        for (var i = 0; i < 130; i++)
        {
            var pressure = 0.08 + (i % 90) * 0.01;
            samples.Add(SampleWithPressure(20 + i * 6.0, 256, i * 4_000L, pressure));
        }

        engine.BeginStroke(brush, samples[0]);
        var dirty = engine.RasterizeSegments(layer, brush, samples, 1, samples.Count - 1);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal("CachedColorTileMajor", engine.LastStats.Path);
        AssertEx.True(engine.LastStats.StampCount > 40,
            $"Expected many unique stamps in one batch, got {engine.LastStats.StampCount}.");
        AssertEx.True(engine.LastStats.CachedDabCount > 32,
            "Batch should exceed color dab cache capacity within one tile-major pass.");
        layer.Pixels.GetPixel(400, 256, out _, out _, out var r, out var a);
        AssertEx.True(a > 0, "Stressed dab cache pass should still deposit pixels.");
        AssertEx.True(r > 100, $"Expected red paint after cache stress, got alpha={a}, red={r}.");
    }

    public static void BrushEngine_BatchedSegmentsMatchSequentialDryStroke()
    {
        using var sequentialEngine = new BrushEngine();
        using var batchedEngine = new BrushEngine();
        using var sequentialLayer = new DrawingLayer("Sequential", 320, 180);
        using var batchedLayer = new DrawingLayer("Batched", 320, 180);
        var brush = new BrushPreset("Dry", 32, 1, 0.75, 0.05, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = false,
            Dynamics = new BrushDynamics()
        };
        var samples = new List<CanvasInputSample>
        {
            Sample(30, 90, 0),
            Sample(80, 84, 1_000),
            Sample(130, 98, 2_000),
            Sample(180, 82, 3_000),
            Sample(240, 94, 4_000)
        };

        sequentialEngine.BeginStroke(brush, samples[0]);
        for (var i = 1; i < samples.Count; i++)
            sequentialEngine.RasterizeSegment(sequentialLayer, brush, samples[i - 1], samples[i]);

        batchedEngine.BeginStroke(brush, samples[0]);
        var dirty = batchedEngine.RasterizeSegments(batchedLayer, brush, samples, 1, samples.Count - 1);

        AssertEx.False(dirty.IsEmpty);
        var bounds = new PixelRegion(0, 0, 320, 180);
        AssertEx.SequenceEqual(sequentialLayer.Pixels.Capture(bounds), batchedLayer.Pixels.Capture(bounds));
    }

    public static void BrushEngine_ColorMixUsesCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Wet cached", 36, 1, 0.75, 0.02, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 1,
            DensityOfPaint = 1,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        AssertEx.False(dirty.IsEmpty);
        AssertEx.Equal("CachedTileMajor", engine.LastStats.Path);
        AssertEx.True(engine.LastStats.CachedDabCount > 10);
        layer.Pixels.GetPixel(240, 128, out _, out _, out _, out var alpha);
        AssertEx.True(alpha > 0);
    }

    public static void DirectDraw_SplitsFastLongBrushSegments()
    {
        var document = new DrawingDocument();
        document.AddLayer();
        var layer = document.ActiveLayer!;
        var ctx = new ToolContext(document);
        var brush = new BrushPreset("Fast low spacing", 36, 1, 1, 0.005, Colors.Black, 0)
        {
            Dynamics = new BrushDynamics()
        };
        var first = Sample(0, 0, 0);
        var txType = typeof(DirectDrawOutput).GetNestedType("StrokeTransaction", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing StrokeTransaction.");
        var tx = Activator.CreateInstance(txType, ctx, layer, document.ActiveLayerIndex, brush, first)
            ?? throw new InvalidOperationException("Could not create StrokeTransaction.");
        var queue = typeof(DirectDrawOutput).GetMethod("QueueNewSamples", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing QueueNewSamples.");
        var samples = new List<CanvasInputSample>
        {
            Sample(0, 0, 0),
            Sample(15_000, 0, 16_000)
        };

        queue.Invoke(null, [tx, layer, brush, samples]);
        var queued = (List<CanvasInputSample>)(txType.GetProperty("QueuedSamples")?.GetValue(tx)
            ?? throw new InvalidOperationException("Missing QueuedSamples."));

        AssertEx.True(queued.Count > 100, $"Expected long fast input to be split, got {queued.Count} queued samples.");
        for (var i = 1; i < queued.Count; i++)
            AssertEx.True(queued[i - 1].DistanceTo(queued[i]) <= 96.001, "Queued brush segment exceeded render budget length.");
    }

    public static void DirectDraw_ColorMixingDoesNotSampleOwnStroke()
    {
        using var engine = new BrushEngine();
        var document = new DrawingDocument();
        document.AddLayer();
        var layer = document.ActiveLayer!;
        for (var y = 26; y <= 34; y++)
        for (var x = 16; x <= 24; x++)
            layer.Pixels.SetPixel(x, y, 0, 0, 0, 255);

        var brush = new BrushPreset("Blend", 8, 1, 1, 0.6, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0,
            DensityOfPaint = 0,
            ColorStretch = 0.1,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var ctx = new ToolContext(document) { Brush = brush, PaintColor = Colors.White };
        var output = new DirectDrawOutput(engine, document);
        var samples = Enumerable.Range(0, 66)
            .Select(i => Sample(20 + i * 2, 30, i * 1_000))
            .ToList();

        output.Execute(ctx, new StrokeInput { RawSamples = samples, SmoothedSamples = samples });

        layer.Pixels.GetPixel(140, 30, out _, out _, out _, out var farAlpha);
        AssertEx.Equal((byte)0, farAlpha, "Color mixing must sample the pre-stroke layer, not feed back from its own slow stroke.");
    }

    public static void CompositeTool_DeactivateDoesNotCancelCompletedOutput()
    {
        var ctx = new ToolContext(new DrawingDocument()) { Brush = new BrushPreset("Ink", 8, 1, 1, 1, Colors.Black, 0) };
        var output = new RecordingOutputProcess();
        var tool = new CompositeTool(new BrushStrokeInputProcess(), output);

        tool.PointerDown(ctx, Sample(10, 10, 0));
        tool.PointerUp(ctx, Sample(12, 10, 1_000));

        AssertEx.Equal(1, output.ExecuteCount);
        AssertEx.False(tool.HasPendingOperation);

        tool.Deactivate(ctx);

        AssertEx.Equal(0, output.CancelCount, "Deactivating an idle tool must not cancel/restore the already completed stroke.");
    }

    public static void CompositeTool_DeactivateDoesNotCancelCompletedOutputWithMoves()
    {
        var ctx = new ToolContext(new DrawingDocument()) { Brush = new BrushPreset("Ink", 8, 1, 1, 1, Colors.Black, 0) };
        var output = new RecordingOutputProcess();
        var tool = new CompositeTool(new BrushStrokeInputProcess(), output);

        // Full stroke with multiple moves, as in real drawing
        tool.PointerDown(ctx, Sample(10, 10, 0));
        tool.PointerMove(ctx, Sample(20, 20, 1_000));
        tool.PointerMove(ctx, Sample(30, 25, 2_000));
        tool.PointerUp(ctx, Sample(40, 30, 3_000));

        AssertEx.Equal(1, output.ExecuteCount, "Up should fire Execute once");
        AssertEx.False(tool.HasPendingOperation, "Tool should be idle after Up");

        // Simulate selecting another tool — calls Deactivate on the old one
        tool.Deactivate(ctx);

        AssertEx.Equal(0, output.CancelCount, "Deactivating a completed-with-moves stroke must NOT cancel it");
    }

    public static void CompositeTool_CommitPreservesDirectDrawPixelsBeforeTempSwitch()
    {
        using var engine = new BrushEngine();
        var document = new DrawingDocument();
        document.AddLayer();
        var layer = document.ActiveLayer!;
        var brush = new BrushPreset("Ink", 12, 1, 1, 0.2, Colors.Black, 0)
        {
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var ctx = new ToolContext(document) { Brush = brush, PaintColor = Colors.Black };
        var tool = new CompositeTool(new BrushStrokeInputProcess(), new DirectDrawOutput(engine, document));

        tool.PointerDown(ctx, Sample(20, 20, 0));
        tool.PointerMove(ctx, Sample(30, 20, 1_000));
        tool.Commit(ctx);

        layer.Pixels.GetPixel(25, 20, out _, out _, out _, out var committedAlpha);
        AssertEx.True(committedAlpha > 0, "Commit should finalize the live direct-draw stroke before a temporary tool switch.");

        tool.Deactivate(ctx);

        layer.Pixels.GetPixel(25, 20, out _, out _, out _, out var afterDeactivateAlpha);
        AssertEx.True(afterDeactivateAlpha > 0, "Deactivating after commit must not restore pre-stroke tiles.");
    }

    public static void CompositeTool_DeactivateCancelsActiveInput()
    {
        var ctx = new ToolContext(new DrawingDocument()) { Brush = new BrushPreset("Ink", 8, 1, 1, 1, Colors.Black, 0) };
        var output = new RecordingOutputProcess();
        var tool = new CompositeTool(new BrushStrokeInputProcess(), output);

        tool.PointerDown(ctx, Sample(10, 10, 0));
        tool.PointerMove(ctx, Sample(12, 10, 1_000));

        AssertEx.True(tool.HasPendingOperation);

        tool.Deactivate(ctx);

        AssertEx.Equal(1, output.CancelCount, "Deactivating during a running transaction should cancel the in-progress preview.");
    }

    public static void BrushEngine_ColorMixAmountControlsDepositedColor()
    {
        using var lowEngine = new BrushEngine();
        using var highEngine = new BrushEngine();
        var lowLayer = new DrawingLayer("Low", 80, 60);
        var highLayer = new DrawingLayer("High", 80, 60);
        for (var y = 26; y <= 34; y++)
        for (var x = 16; x <= 24; x++)
        {
            lowLayer.Pixels.SetPixel(x, y, 0, 0, 0, 255);
            highLayer.Pixels.SetPixel(x, y, 0, 0, 0, 255);
        }

        BrushPreset Brush(double amount) => new("Mix", 8, 1, 1, 0.6, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = amount,
            DensityOfPaint = 1,
            ColorStretch = 0,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var sample = Sample(20, 30, 0);

        lowEngine.BeginStroke(Brush(0), sample);
        highEngine.BeginStroke(Brush(1), sample);
        lowEngine.RasterizeDab(lowLayer, Brush(0), sample, velocity: 0);
        highEngine.RasterizeDab(highLayer, Brush(1), sample, velocity: 0);

        lowLayer.Pixels.GetPixel(20, 30, out _, out _, out var lowRed, out _);
        highLayer.Pixels.GetPixel(20, 30, out _, out _, out var highRed, out _);
        AssertEx.True(highRed > lowRed, "Amount of paint should increase drawing color contribution.");
    }

    public static void BrushEngine_DoesNotDisposeTipOwnedCachedMasks()
    {
        var tip = new CachedCountingBrushTip();
        var brush = new BrushPreset("Cached", 80, 1, 0.7, 0.1, Colors.Black, 0)
        {
            Tip = tip,
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 400, 240);

        using (var engine = new BrushEngine())
        {
            var from = Sample(40, 120, 0);
            var to = Sample(300, 120, 16_000);
            engine.BeginStroke(brush, from);
            engine.RasterizeSegment(layer, brush, from, to);
            engine.EndStroke();

            from = Sample(40, 150, 32_000);
            to = Sample(300, 150, 48_000);
            engine.BeginStroke(brush, from);
            var dirty = engine.RasterizeSegment(layer, brush, from, to);
            AssertEx.False(dirty.IsEmpty);
        }

        AssertEx.False(tip.CachedMaskDisposed, "BrushEngine must not dispose masks owned by the brush tip cache.");
        tip.Dispose();
    }

    public static void ToolController_DoesNotOverridePresetBrushState()
    {
        var document = new DrawingDocument();
        var context = new ToolContext(document)
        {
            Brush = new BrushPreset("Source", 10, 1, 1, 0.1, Colors.Black, 0)
        };
        var first = new PresetApplyingTool();
        var second = new PresetApplyingTool();
        var controller = new ToolController(context, first);
        var targetPreset = new ToolPreset
        {
            Name = "Smudge",
            InputProcess = InputProcessType.Brush,
            OutputProcess = OutputProcessType.DirectDraw,
            BrushOverride = new BrushPresetOverrideDocument
            {
                Size = 80,
                ColorMix = true,
                SmudgeMode = SmudgeMode.Smear,
                AmountOfPaint = 0.25,
                DensityOfPaint = 0
            }
        };

        controller.SetActiveTool(second, targetPreset);

        AssertEx.Near(80, context.Brush.Size);
        AssertEx.True(context.Brush.ColorMix);
        AssertEx.Equal(SmudgeMode.Smear, context.Brush.SmudgeMode);
        AssertEx.Near(0.25, context.Brush.AmountOfPaint);
        AssertEx.Near(0, context.Brush.DensityOfPaint);
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) AlphaBounds(DrawingLayer layer)
    {
        var minX = layer.Width;
        var minY = layer.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < layer.Height; y++)
        for (var x = 0; x < layer.Width; x++)
        {
            layer.Pixels.GetPixel(x, y, out _, out _, out _, out var a);
            if (a == 0) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return (minX, minY, maxX, maxY);
    }

    private sealed class CountingBrushTip : IBrushTip
    {
        public int GenerateCount { get; private set; }

        public SKBitmap GenerateMask(int baseSize, float hardness)
        {
            GenerateCount++;
            var bitmap = new SKBitmap(new SKImageInfo(baseSize, baseSize, SKColorType.Alpha8, SKAlphaType.Unpremul));
            bitmap.Erase(new SKColor(255, 255, 255, 255));
            return bitmap;
        }
    }

    private sealed class CachedCountingBrushTip : IBrushTip, IDisposable
    {
        private SKBitmap? _cached;

        public bool CachedMaskDisposed => _cached?.Handle == IntPtr.Zero;

        public SKBitmap GenerateMask(int baseSize, float hardness)
        {
            if (_cached != null) return _cached;

            _cached = new SKBitmap(new SKImageInfo(baseSize, baseSize, SKColorType.Alpha8, SKAlphaType.Unpremul));
            _cached.Erase(new SKColor(255, 255, 255, 255));
            return _cached;
        }

        public void Dispose() => _cached?.Dispose();
    }

    private sealed class PresetApplyingTool : ITool
    {
        public void PointerDown(ToolContext ctx, CanvasInputSample s) { }
        public void PointerMove(ToolContext ctx, CanvasInputSample s) { }
        public void PointerUp(ToolContext ctx, CanvasInputSample s) { }
        public void Cancel(ToolContext ctx) { }
        public void RenderOverlay(Avalonia.Media.DrawingContext dc, ToolContext ctx, double zoom) { }

        public void Activate(ToolContext ctx)
        {
            if (ctx.ActivePreset != null)
                ctx.Brush = ctx.ActivePreset.ApplyToBrushPreset(ctx.Brush);
        }
    }

    private sealed class RecordingOutputProcess : IOutputProcess
    {
        public bool Antialiasing { get; set; } = true;
        public int ExecuteCount { get; private set; }
        public int CancelCount { get; private set; }

        public void Preview(ToolContext ctx, IProcessedInput input) { }
        public void Execute(ToolContext ctx, IProcessedInput input) => ExecuteCount++;
        public void Cancel() => CancelCount++;
    }

    private static CanvasInputSample Sample(double x, double y, long timeMicros)
        => new(x, y, 1, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);

    private static CanvasInputSample SampleWithPressure(double x, double y, long timeMicros, double pressure)
        => new(x, y, pressure, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);

    public static unsafe void ImageBrushTip_PreservesAspectRatio()
    {
        using var source = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.Erase(SKColors.Transparent);
        var ptr = (byte*)source.GetPixels().ToPointer();
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var p = ptr + y * source.RowBytes + x * 4;
            p[0] = 255;
            p[1] = 255;
            p[2] = 255;
            p[3] = 255;
        }

        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var tip = new ImageBrushTip(data.ToArray());
        using var mask = tip.GenerateMask(8, 1.0f);

        var maskPtr = (byte*)mask.GetPixels().ToPointer();
        AssertEx.Equal((byte)0, maskPtr[0 * mask.RowBytes + 4]);
        AssertEx.Equal((byte)255, maskPtr[4 * mask.RowBytes + 4]);
        AssertEx.Equal((byte)0, maskPtr[7 * mask.RowBytes + 4]);
    }

    public static void ImageBrushTip_MasksRemainStableAcrossSizes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(1, 0, SKColors.White);
        bitmap.SetPixel(1, 1, SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var tip = new ImageBrushTip(data.ToArray());
        var first = tip.GenerateMask(24, 1.0f);
        var firstHandle = first.Handle;

        var second = tip.GenerateMask(64, 1.0f);
        var third = tip.GenerateMask(24, 0.5f);
        var firstAgain = tip.GenerateMask(24, 1.0f);

        AssertEx.True(firstHandle != IntPtr.Zero);
        AssertEx.True(second.Handle != IntPtr.Zero);
        AssertEx.True(third.Handle != IntPtr.Zero);
        AssertEx.Equal(firstHandle, first.Handle, "Requesting a different cursor/preview mask must not dispose an active stroke mask.");
        AssertEx.Equal(firstHandle, firstAgain.Handle, "The original mask should remain cached for its size/hardness key.");
    }

    public static void ImageBrushTip_DoesNotDisposeCachedMasksUnderChurn()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var tip = new ImageBrushTip(data.ToArray());

        var first = tip.GenerateMask(16, 1.0f);
        var firstHandle = first.Handle;
        for (var size = 17; size < 80; size++)
            _ = tip.GenerateMask(size, 1.0f);

        AssertEx.Equal(firstHandle, first.Handle, "Image tip cache churn must not dispose a mask that a stroke may still reference.");
        AssertEx.True(first.GetPixels() != IntPtr.Zero, "Original image tip mask pixels should remain valid after cache churn.");
    }

    public static void ProceduralBrushTip_DoesNotDisposeCachedMasksUnderChurn()
    {
        var tip = new ProceduralBrushTip();
        var first = tip.GenerateMask(16, 1.0f);
        var firstHandle = first.Handle;
        for (var size = 17; size < 80; size++)
            _ = tip.GenerateMask(size, 1.0f);

        AssertEx.Equal(firstHandle, first.Handle, "Procedural tip cache churn must not dispose a mask that a stroke may still reference.");
        AssertEx.True(first.GetPixels() != IntPtr.Zero, "Original procedural tip mask pixels should remain valid after cache churn.");
    }

    public static void ProceduralBrushTip_SoftRoundDiffersFromRound()
    {
        var round = new ProceduralBrushTip(BrushTipShape.Circle).GenerateMask(64, 0.85f);
        var soft = new ProceduralBrushTip(BrushTipShape.SoftRound).GenerateMask(64, 0.85f);

        var roundAlpha = AlphaAt(round, 51, 32);
        var softAlpha = AlphaAt(soft, 51, 32);

        AssertEx.True(roundAlpha > 240, $"Round should keep a hard body at this radius, got {roundAlpha}.");
        AssertEx.True(softAlpha < roundAlpha - 80, $"Soft round should fall off before the edge, got round={roundAlpha}, soft={softAlpha}.");
    }

    public static void ProceduralBrushTip_FlatIsRectangular()
    {
        var flat = new ProceduralBrushTip(BrushTipShape.Flat).GenerateMask(96, 0.95f);
        var bounds = AlphaBounds(flat, threshold: 64);
        var width = bounds.MaxX - bounds.MinX + 1;
        var height = bounds.MaxY - bounds.MinY + 1;

        AssertEx.True(width > height * 3.5, $"Flat tip should be a wide rectangle, got {width}x{height}.");
        AssertEx.True(AlphaAt(flat, 48, bounds.MinY + 1) > 180, "Flat top edge should be filled, not oval-tapered.");
        AssertEx.True(AlphaAt(flat, 48, bounds.MaxY - 1) > 180, "Flat bottom edge should be filled, not oval-tapered.");
    }

    public static void ProceduralBrushTip_BristleHasSeparatedStrands()
    {
        var bristle = new ProceduralBrushTip(BrushTipShape.Bristle).GenerateMask(96, 0.9f);
        var runs = CountVerticalAlphaRuns(bristle, x: 48, threshold: 32);

        AssertEx.True(runs >= 5, $"Bristle tip should contain separated strands, got {runs} alpha runs.");
    }

    public static void ProceduralBrushTip_IsGraphBacked()
    {
        var procedural = new ProceduralBrushTip(BrushTipShape.Flat, 1.0f);
        var fromGraph = new NodeBrushTip(procedural.Graph);

        AssertEx.Equal(BrushTipNodeKind.BoxDistanceField, procedural.Graph.Nodes.Single(n => n.Id == "flat-field").Kind);
        AssertEx.SequenceEqual(AlphaBytes(procedural.GenerateMask(80, 0.82f)), AlphaBytes(fromGraph.GenerateMask(80, 0.82f)));
    }

    public static void ProceduralBrushTipData_StoresGraphPayload()
    {
        var tip = new ProceduralBrushTip(BrushTipShape.Bristle, 1.0f);
        var data = BrushTipData.FromTip(tip);
        var restored = data.CreateTip();

        AssertEx.Equal(BrushTipStorageKind.NodeGraph, data.Kind);
        AssertEx.True(data.NodeGraph != null, "Procedural brush tips should save their graph payload.");
        AssertEx.True(restored is NodeBrushTip, "Graph payloads should restore as editable node graph tips.");
        AssertEx.SequenceEqual(AlphaBytes(tip.GenerateMask(72, 0.74f)), AlphaBytes(restored.GenerateMask(72, 0.74f)));
    }

    public static void BrushTipNodeGraph_ValidatesBadTopology()
    {
        var graph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["missing"] },
                new BrushTipNode { Id = "cycle-a", Kind = BrushTipNodeKind.Add, Inputs = ["cycle-b", "output"] },
                new BrushTipNode { Id = "cycle-b", Kind = BrushTipNodeKind.Add, Inputs = ["cycle-a", "output"] }
            ]
        };
        var errors = graph.Validate();

        AssertEx.True(errors.Any(e => e.Contains("missing", StringComparison.Ordinal)), "Validator should report missing input ids.");
        graph.OutputNodeId = "cycle-a";
        errors = graph.Validate();
        AssertEx.True(errors.Any(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase)), "Validator should report cycles reachable from output.");
    }

    public static void BrushTipNodeGraph_CacheKeyChangesWithContent()
    {
        var circle = BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Circle);
        var soft = BrushTipNodeGraph.FromProceduralShape(BrushTipShape.SoftRound);
        AssertEx.False(circle.CacheKey() == soft.CacheKey(), "Different preset shapes should produce different cache keys.");

        var edited = circle.DeepClone();
        edited.Nodes[0].Hardness = 0.42f;
        AssertEx.False(circle.CacheKey() == edited.CacheKey(), "Graph edits should change the cache key.");
    }

    public static void BrushTipNodePorts_EnforceCompatibility()
    {
        AssertEx.True(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Coordinates, BrushTipNodeKind.DistanceField, 0));
        AssertEx.False(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Coordinates, BrushTipNodeKind.SmoothStep, 0));
        AssertEx.True(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Circle, BrushTipNodeKind.SmoothStep, 0));
        AssertEx.False(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Circle, BrushTipNodeKind.RotateCoordinates, 0));
        AssertEx.True(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Noise, BrushTipNodeKind.WarpCoordinates, 1));
        AssertEx.False(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Coordinates, BrushTipNodeKind.WarpCoordinates, 1));
    }

    public static void NodeBrushTip_EvaluatesDeterministicGraph()
    {
        var tip = new NodeBrushTip(BrushTipNodeGraph.BristleRound());
        var first = tip.GenerateMask(96, 0.8f);
        var second = tip.GenerateMask(96, 0.8f);
        var third = new NodeBrushTip(BrushTipNodeGraph.BristleRound()).GenerateMask(96, 0.8f);

        AssertEx.Equal(first.Handle, second.Handle, "Node tips should reuse cached masks for identical size/hardness/graph.");
        AssertEx.SequenceEqual(AlphaBytes(first), AlphaBytes(third));
    }

    public static void NodeBrushTip_ComposesProceduralPrimitives()
    {
        var graph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "round", Kind = BrushTipNodeKind.Circle, Radius = 0.48f, Hardness = 0.9f },
                new BrushTipNode { Id = "noise", Kind = BrushTipNodeKind.Noise, Density = 0.45f, Opacity = 1f, Scale = 1.5f, Seed = 123 },
                new BrushTipNode { Id = "grain", Kind = BrushTipNodeKind.Multiply, Inputs = ["round", "noise"] },
                new BrushTipNode { Id = "cut", Kind = BrushTipNodeKind.Threshold, Inputs = ["grain"], Threshold = 0.08f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["cut"] }
            ]
        };
        var mask = new NodeBrushTip(graph).GenerateMask(96, 0.85f);
        var bounds = AlphaBounds(mask, threshold: 16);
        var center = AlphaAt(mask, 48, 48);
        var corner = AlphaAt(mask, 3, 3);

        AssertEx.True(bounds.MaxX > bounds.MinX && bounds.MaxY > bounds.MinY, "Composed node tip should render visible coverage.");
        AssertEx.True(center > corner, $"Circle multiplied by noise should stay stronger near center than corner, got center={center}, corner={corner}.");
    }

    public static void NodeBrushTip_SupportsCoordinateWarping()
    {
        var plainGraph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "field", Kind = BrushTipNodeKind.DistanceField, Width = 1f, Height = 1f },
                new BrushTipNode { Id = "edge", Kind = BrushTipNodeKind.SmoothStep, Inputs = ["field"], Threshold = 0.48f, Hardness = 0.03f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["edge"] }
            ]
        };
        var warpedGraph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "coords", Kind = BrushTipNodeKind.Coordinates },
                new BrushTipNode { Id = "bands", Kind = BrushTipNodeKind.Stripe, Inputs = ["coords"], Scale = 9f, Density = 0.55f, Opacity = 1f },
                new BrushTipNode { Id = "warp", Kind = BrushTipNodeKind.WarpCoordinates, Inputs = ["coords", "bands"], Density = 0.8f, Width = 0.28f, Height = 0.12f, RotationDegrees = 0f },
                new BrushTipNode { Id = "field", Kind = BrushTipNodeKind.DistanceField, Inputs = ["warp"], Width = 1f, Height = 1f },
                new BrushTipNode { Id = "edge", Kind = BrushTipNodeKind.SmoothStep, Inputs = ["field"], Threshold = 0.48f, Hardness = 0.03f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["edge"] }
            ]
        };

        var plain = new NodeBrushTip(plainGraph).GenerateMask(96, 0.85f);
        var warped = new NodeBrushTip(warpedGraph).GenerateMask(96, 0.85f);

        AssertEx.True(warpedGraph.Validate().Count == 0, "Coordinate-warp graph should validate.");
        AssertEx.True(!AlphaBytes(plain).SequenceEqual(AlphaBytes(warped)), "Warped coordinate graph should produce a different mask than the unwarped field.");
    }

    public static void NodeBrushTip_SamplesEmbeddedImageTip()
    {
        var png = ColoredTipPngBytes(SKColors.Black);
        var image = new ImageBrushTip(png).GenerateMask(48, 0.9f);
        var graph = BrushTipNodeGraph.FromImageTip(png);
        var node = new NodeBrushTip(graph).GenerateMask(48, 0.9f);

        AssertEx.True(graph.Validate().Count == 0, "Image sampler graph should validate.");
        AssertEx.SequenceEqual(AlphaBytes(image), AlphaBytes(node));
        AssertEx.True(graph.TryGetDirectImageSampler(out var restored));
        AssertEx.Equal(png.Length, restored.Length);
    }

    public static void BrushMaterialTips_ResolveActiveEmbeddedTip()
    {
        var png = ColoredTipPngBytes(SKColors.Red);
        var preset = new BrushPreset("Imported", 32, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new NodeBrushTip(BrushTipNodeGraph.FromImageTip(png)),
            Tips = []
        };

        var options = ImageSamplerOptions.FromTips(BrushMaterialTips.ForPreset(preset));
        AssertEx.Equal(1, options.Count);
        AssertEx.True(ImageSamplerOptions.SameBytes(png, options[0].Tip.PngBytes));
    }

    public static void NodeBrushTip_EvaluateColorWithProceduralOutput()
    {
        var graph = new BrushTipNodeGraph();
        graph.Nodes.Add(new BrushTipNode
        {
            Id = "tip-image",
            Kind = BrushTipNodeKind.ImageSampler,
            PngBytes = ColoredTipPngBytes(SKColors.Red)
        });

        using var tip = new NodeBrushTip(graph);
        AssertEx.True(BrushTipNodeGraphEvaluator.GraphUsesColor(graph));
        AssertEx.True(tip.HasColor);
        AssertEx.False(tip.IsDirectImageSampler, "Procedural output should not take the direct-image fast path.");

        using var stamp = tip.GenerateColorStamp(48);
        AssertEx.True(stamp != null, "Color evaluation should not crash and must return a bitmap.");
        AssertEx.Equal(48, stamp!.Width);
        AssertEx.Equal(48, stamp.Height);
    }

    public static void ImageBrushTip_ColorStampPreservesColor()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 80, 80);
        var brush = new BrushPreset("Red stamp", 24, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new ImageBrushTip(ColoredTipPngBytes(SKColors.Red)),
            Shape = null
        };
        var sample = Sample(40, 40, 0);

        engine.BeginStroke(brush, sample);
        engine.RasterizeDab(layer, brush, sample, velocity: 0);

        layer.Pixels.GetPixel(40, 40, out var b, out var g, out var r, out var a);
        AssertEx.True(a > 0, "Colored stamp should deposit alpha.");
        AssertEx.True(r > 180 && g < 80 && b < 80, $"Colored image tip should preserve red pixels, got BGRA=({b},{g},{r},{a}).");
    }

    private static byte[] ColoredTipPngBytes(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static unsafe byte AlphaAt(SKBitmap bitmap, int x, int y)
    {
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        return ptr[y * bitmap.RowBytes + x];
    }

    private static unsafe byte[] AlphaBytes(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height];
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = ptr + y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x++)
                bytes[y * bitmap.Width + x] = row[x];
        }
        return bytes;
    }

    private static unsafe (int MinX, int MinY, int MaxX, int MaxY) AlphaBounds(SKBitmap bitmap, byte threshold)
    {
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = ptr + y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (row[x] <= threshold) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return (minX, minY, maxX, maxY);
    }

    private static int CountVerticalAlphaRuns(SKBitmap bitmap, int x, byte threshold)
    {
        var runs = 0;
        var inRun = false;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var on = AlphaAt(bitmap, x, y) > threshold;
            if (on && !inRun)
                runs++;
            inRun = on;
        }
        return runs;
    }

    public static void AbrPresetMapping_KeepsDynamics()
    {
        var importer = typeof(AbrImporter);
        var paramType = importer.GetNestedType("AbrBrushParams", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing AbrBrushParams.");
        var vrType = importer.GetNestedType("VrParams", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing VrParams.");
        var p = Activator.CreateInstance(paramType)
            ?? throw new InvalidOperationException("Could not create ABR params.");

        SetField(p, "HasDiameter", true);
        SetField(p, "Diameter", 32.0);
        SetField(p, "HasAngle", true);
        SetField(p, "Angle", 15.0);
        SetField(p, "HasRoundness", true);
        SetField(p, "Roundness", 40.0);
        SetField(p, "HasSpacing", true);
        SetField(p, "Spacing", 8.0);
        SetField(p, "UseScatter", true);
        SetField(p, "ScatterDist", 35.0);

        SetField(p, "SizeDyn", Vr(vrType, control: 2, jitter: 25, minimum: 20));
        SetField(p, "AngleDyn", Vr(vrType, control: 7, jitter: 50, minimum: 0));
        SetField(p, "SpacingDyn", Vr(vrType, control: 0, jitter: 40, minimum: 0));

        var build = importer.GetMethod("BuildPreset", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing BuildPreset.");
        var args = new object?[] { "ABR", Array.Empty<byte>(), p, 25, null, null, null };
        build.Invoke(null, args);

        var preset = (BrushPreset)(args[4] ?? throw new InvalidOperationException("No preset returned."));
        AssertEx.Equal(32.0, preset.Size);
        AssertEx.Near(0.08, preset.Spacing);
        AssertEx.Near(15.0, preset.Angle);
        AssertEx.Near(0.40, preset.TipThickness);
        AssertEx.Equal(BrushTipDirection.Horizontal, preset.TipDirection);
        AssertEx.Equal(BrushDynamics.AngleSource.DirectionOfLine, preset.BaseAngleSource);
        AssertEx.True(preset.AngleJitter > 0.4f);
        AssertEx.True(preset.Dynamics.Size.IsEnabled);
        AssertEx.True(preset.Dynamics.Scatter.IsEnabled);
        AssertEx.True(preset.Dynamics.Spacing.IsEnabled);
        AssertEx.True(preset.Dynamics.Size.Sensors.Any(s => s.Type == SensorType.Pressure));
        AssertEx.True(preset.Dynamics.Size.Sensors.Any(s => s.Type == SensorType.Random));
    }

    public static void AbrMaskCleanup_InvertsDarkOnLightMasks()
    {
        var importer = typeof(AbrImporter);
        var clean = importer.GetMethod("CleanTipMask", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing CleanTipMask.");
        var pixels = new byte[]
        {
            255, 255, 255, 255,
            255,   0,   0, 255,
            255,   0,   0, 255,
            255, 255, 255, 255
        };

        var result = clean.Invoke(null, [pixels, 4, 4])
            ?? throw new InvalidOperationException("No cleanup result.");
        var tuple = (ITuple)result;

        var cleaned = (byte[])tuple[0]!;
        AssertEx.Equal(4, (int)tuple[1]!);
        AssertEx.Equal(4, (int)tuple[2]!);
        AssertEx.Equal((byte)0, cleaned[0]);
        AssertEx.Equal((byte)255, cleaned[1 * 4 + 1]);
        AssertEx.Equal((byte)255, cleaned[2 * 4 + 2]);
        AssertEx.Equal((byte)0, cleaned[3 * 4 + 3]);
    }

    private static object Vr(Type vrType, int control, double jitter, double minimum)
    {
        var vr = Activator.CreateInstance(vrType)
            ?? throw new InvalidOperationException("Could not create VR params.");
        SetField(vr, "ControlType", control);
        SetField(vr, "Jitter", jitter);
        SetField(vr, "Minimum", minimum);
        return vr;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        field.SetValue(target, value);
    }

    private static StrokePoint StrokePoint(
        float pressure = 0.6f,
        float speed = 0.4f,
        float random = 0.3f)
        => new(10, 20, pressure, 45, -45, 90, MathF.PI, speed, 150, 10, random, 0.8f);
}

internal static class DrawingDocumentTests
{
    public static void Constructor_SetsInitialLayerState()
    {
        var document = new DrawingDocument(20, 10);
        AssertEx.Equal(20, document.Width);
        AssertEx.Equal(10, document.Height);
        AssertEx.Equal(0, document.Layers.Count);
        AssertEx.Equal(-1, document.ActiveLayerIndex);
        AssertEx.False(document.CanPaintActiveLayer);
        AssertEx.False(document.CanUndo);
        AssertEx.False(document.IsDirty);
    }

    public static void LayerManagement_WorksForCommonMutations()
    {
        var document = new DrawingDocument(4, 4);
        var layersChanged = 0;
        document.LayersChanged += (_, _) => layersChanged++;

        document.AddLayer();
        AssertEx.Equal(1, document.Layers.Count);
        AssertEx.Equal(0, document.ActiveLayerIndex);

        document.AddLayer();
        AssertEx.Equal(2, document.Layers.Count);
        AssertEx.Equal(1, document.ActiveLayerIndex);

        document.SelectLayer(0);
        document.DuplicateActiveLayer();
        AssertEx.Equal(3, document.Layers.Count);
        AssertEx.Equal("Layer 1 copy", document.ActiveLayer!.Name);

        document.DeleteActiveLayer();
        AssertEx.Equal(2, document.Layers.Count);
        AssertEx.True(layersChanged >= 4);
    }

    public static void CapabilityFlags_RespectLayerState()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ToggleLayerLock(0);
        AssertEx.False(document.CanPaintActiveLayer);
        AssertEx.False(document.CanDeleteLayer);
        document.ToggleLayerLock(0);

        document.AddGroupLayer();
        AssertEx.False(document.CanPaintActiveLayer);
        AssertEx.False(document.CanModifyActiveLayer);
    }

    public static void LayerPropertyMutations_UndoRedo()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.MarkAsSaved();
        document.SetActiveLayerName("Ink");
        document.SetActiveLayerOpacity(2);
        document.SetActiveLayerBlendMode("Multiply");

        AssertEx.Equal("Ink", document.ActiveLayer.Name);
        AssertEx.Equal(1.0, document.ActiveLayer.Opacity);
        AssertEx.Equal("Multiply", document.ActiveLayer.BlendMode);
        AssertEx.True(document.IsDirty);

        document.Undo();
        AssertEx.Equal("Normal", document.ActiveLayer.BlendMode);
        document.Redo();
        AssertEx.Equal("Multiply", document.ActiveLayer.BlendMode);
    }

    public static void ReferenceLayerFlag_UndoRedoAndDuplicate()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ToggleLayerReference(0);
        AssertEx.True(document.ActiveLayer!.IsReference);

        document.Undo();
        AssertEx.False(document.ActiveLayer.IsReference);
        document.Redo();
        AssertEx.True(document.ActiveLayer.IsReference);

        document.DuplicateActiveLayer();
        AssertEx.True(document.ActiveLayer.IsReference);
    }

    public static void SelectionMutations_UndoRedo()
    {
        var document = new DrawingDocument(8, 8);
        var changed = 0;
        document.SelectionChanged += (_, _) => changed++;

        var before = document.Selection.CaptureSnapshot();
        document.Selection.SetFromRect(1, 1, 3, 3);
        document.CommitSelectionMutation(before);

        AssertEx.True(document.Selection.HasSelection);
        AssertEx.True(document.Selection.IsSelected(2, 2));
        AssertEx.False(document.Selection.IsSelected(5, 5));
        AssertEx.True(document.CanUndo);

        document.Undo();
        AssertEx.False(document.Selection.HasSelection);

        document.Redo();
        AssertEx.True(document.Selection.HasSelection);
        AssertEx.True(document.Selection.IsSelected(2, 2));
        AssertEx.True(changed >= 3);
    }

    public static void SelectionInvert_KeepsRenderableMaskGeometry()
    {
        var document = new DrawingDocument(8, 8);
        document.Selection.SetFromRect(2, 2, 2, 2);
        document.Selection.Invert();

        var snapshot = document.Selection.CaptureSnapshot();
        AssertEx.True(document.Selection.HasSelection);
        AssertEx.Equal("Mask", snapshot.GeometryType);
        AssertEx.False(document.Selection.IsSelected(2, 2));
        AssertEx.True(document.Selection.IsSelected(0, 0));
        AssertEx.Equal(new SKRectI(0, 0, 8, 8), document.Selection.GetMaskBounds());

        document.Selection.Clear();
        document.Selection.Invert();
        AssertEx.True(document.Selection.HasSelection);
        AssertEx.Equal("Mask", document.Selection.CaptureSnapshot().GeometryType);
        AssertEx.Equal(new SKRectI(0, 0, 8, 8), document.Selection.GetMaskBounds());

        document.Selection.SetFromRect(2, 2, 2, 2);
        document.Selection.Invert();
        document.Selection.Invert();
        AssertEx.Equal("Rect", document.Selection.CaptureSnapshot().GeometryType);
        AssertEx.Equal(new SKRectI(2, 2, 4, 4), document.Selection.GetMaskBounds());
    }

    public static void Transform_StartsOnEmptySelection()
    {
        var document = new DrawingDocument(16, 16);
        document.AddLayer();
        document.Selection.SetFromRect(4, 4, 6, 6);
        var ctx = new ToolContext(document);
        var tool = new TransformTool();

        AssertEx.True(tool.BeginTransform(ctx), "Selection transform should start from the selected region even when it contains no pixels.");
        AssertEx.True(tool.HasPendingOperation, "Transform overlay should be active for an empty selected region.");
    }

    public static void ClearActiveLayer_UndoRedoRestoresPixels()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ActiveLayer.Pixels.SetPixel(1, 1, 1, 2, 3, 255);
        document.ClearActiveLayer();

        document.ActiveLayer.Pixels.GetPixel(1, 1, out _, out _, out _, out var clearedAlpha);
        AssertEx.Equal((byte)0, clearedAlpha);
        AssertEx.True(document.CanUndo);

        document.Undo();
        document.ActiveLayer.Pixels.GetPixel(1, 1, out var b, out var g, out var r, out var a);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b, g, r, a]);

        document.Redo();
        document.ActiveLayer.Pixels.GetPixel(1, 1, out _, out _, out _, out var redoneAlpha);
        AssertEx.Equal((byte)0, redoneAlpha);
    }

    public static void MoveLayer_ValidatesTargetsAndMoves()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();

        AssertEx.False(document.CanMoveLayer(1, 1, LayerDropPlacement.Above));
        AssertEx.False(document.CanMoveLayer(-1, 1, LayerDropPlacement.Above));

        var moving = document.ActiveLayer;
        document.MoveLayer(2, 0, LayerDropPlacement.Below);
        AssertEx.Equal(0, document.ActiveLayerIndex);
        AssertEx.True(ReferenceEquals(moving, document.ActiveLayer));
    }

    public static void GroupSelectedLayers_CreatesGroupAndUndoRestores()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();

        document.GroupSelectedLayers([0, 1, 2]);
        AssertEx.Equal(4, document.Layers.Count);
        AssertEx.True(document.ActiveLayer!.IsGroup);
        AssertEx.Equal(3, document.ActiveLayer.Children.Count);
        AssertEx.Equal(1, document.ActiveLayer.Children[0].IndentLevel);

        document.Undo();
        AssertEx.Equal(3, document.Layers.Count);
        AssertEx.False(document.Layers.Any(layer => layer.IsGroup));
    }

    public static void HistoryKind_MarksUndoAndRedo()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        AssertEx.Equal(DocumentHistoryChangeKind.Mutation, document.LastHistoryChangeKind);

        document.Undo();
        AssertEx.Equal(DocumentHistoryChangeKind.Undo, document.LastHistoryChangeKind);

        document.Redo();
        AssertEx.Equal(DocumentHistoryChangeKind.Redo, document.LastHistoryChangeKind);
    }

    public static void ImportLifecycle_ReplacesDocumentState()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ClearForImport();
        AssertEx.Equal(0, document.Layers.Count);
        AssertEx.False(document.CanUndo);

        document.ResizeForImport(8, 9);
        var imported = document.AddLayerForImport("Imported", bitmapWidth: 2, bitmapHeight: 3);
        imported.Pixels.SetPixel(1, 2, 9, 8, 7, 255);
        document.FinalizeImport();

        AssertEx.Equal(8, document.Width);
        AssertEx.Equal(9, document.Height);
        AssertEx.Equal("Imported", document.ActiveLayer.Name);
        document.ActiveLayer.Pixels.GetPixel(1, 2, out var b, out var g, out var r, out var a);
        AssertEx.SequenceEqual(new byte[] { 9, 8, 7, 255 }, [b, g, r, a]);
    }

    public static void DuplicateActiveLayer_CopiesPixels()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ActiveLayer.Pixels.SetPixel(2, 2, 1, 2, 3, 255);
        document.DuplicateActiveLayer();

        document.ActiveLayer.Pixels.GetPixel(2, 2, out var b, out var g, out var r, out var a);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b, g, r, a]);
        document.Layers[0].Pixels.SetPixel(2, 2, 0, 0, 0, 0);
        document.ActiveLayer.Pixels.GetPixel(2, 2, out _, out _, out _, out var duplicateAlpha);
        AssertEx.Equal((byte)255, duplicateAlpha);
    }

    public static void AddBackgroundLayer_FillsWhiteAndTilesStayIndependent()
    {
        var document = new DrawingDocument(128, 64);
        document.AddBackgroundLayer();

        var background = document.Layers[0];
        background.Pixels.GetPixel(0, 0, out var b1, out var g1, out var r1, out var a1);
        background.Pixels.GetPixel(64, 0, out var b2, out var g2, out var r2, out var a2);
        AssertEx.SequenceEqual(new byte[] { 255, 255, 255, 255 }, [b1, g1, r1, a1]);
        AssertEx.SequenceEqual(new byte[] { 255, 255, 255, 255 }, [b2, g2, r2, a2]);

        background.Pixels.Clear(new PixelRegion(0, 0, 1, 1));
        background.Pixels.GetPixel(0, 0, out _, out _, out _, out var clearedAlpha);
        background.Pixels.GetPixel(64, 0, out b2, out g2, out r2, out a2);
        AssertEx.Equal((byte)0, clearedAlpha);
        AssertEx.SequenceEqual(new byte[] { 255, 255, 255, 255 }, [b2, g2, r2, a2]);
    }

    public static void ToolFactory_EyedropperOptions()
    {
        var document = new DrawingDocument(4, 4);
        var factory = new Floss.App.Processes.ToolFactory(document, new BrushEngine());
        var tool = factory.CreateTool(new ToolPreset
        {
            InputProcess = InputProcessType.Click,
            OutputProcess = OutputProcessType.Eyedropper,
            EyedropperSampleMode = EyedropperSampleMode.CurrentLayer,
            EyedropperExcludeLockedLayers = true,
            EyedropperExcludeReferenceLayers = true
        });

        var output = ((Floss.App.Processes.CompositeTool)tool).Output as Floss.App.Processes.Output.EyedropperOutput
            ?? throw new InvalidOperationException("Expected eyedropper output.");
        AssertEx.Equal(EyedropperSampleMode.CurrentLayer, output.SampleMode);
        AssertEx.True(output.ExcludeLockedLayers);
        AssertEx.True(output.ExcludeReferenceLayers);
    }
}

internal static class TimelapseTests
{
    public static void SelectFrames_SamplesRequestedDuration()
    {
        var frames = Enumerable.Range(0, 500)
            .Select(i => $"frame_{i:D6}.png")
            .ToArray();

        var selected = TimelapseSession.SelectFrames(frames, TimelapseLength.Seconds15);

        AssertEx.Equal(180, selected.Count);
        AssertEx.Equal("frame_000000.png", selected[0]);
        AssertEx.Equal("frame_000499.png", selected[^1]);
    }

    public static void ComposeFrame_RespectsAspectModes()
    {
        using var source = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        source.Erase(SKColors.Red);

        using var landscape = TimelapseSession.ComposeFrame(source, new TimelapseExportSettings
        {
            Aspect = TimelapseAspect.Landscape,
            LongestSidePixels = 160
        });
        using var portrait = TimelapseSession.ComposeFrame(source, new TimelapseExportSettings
        {
            Aspect = TimelapseAspect.Portrait,
            LongestSidePixels = 160
        });

        AssertEx.Equal(160, landscape.Width);
        AssertEx.Equal(90, landscape.Height);
        AssertEx.Equal(90, portrait.Width);
        AssertEx.Equal(160, portrait.Height);
    }
}

internal static class PsdExporterTests
{
    public static void Export_CanBeReadBack()
    {
        var bytes = ExportSamplePsd();
        using var stream = new MemoryStream(bytes);

        var psd = PsdReader.Read(stream);

        AssertEx.Equal(2, psd.Width);
        AssertEx.Equal(2, psd.Height);
        AssertEx.Equal(1, psd.Layers.Count);

        var layer = psd.Layers[0] as PsdLayerNode
            ?? throw new InvalidOperationException("Expected exported PSD to contain a paint layer.");
        AssertEx.Equal("Ink", layer.Name);
        AssertEx.Equal("mul ", layer.BlendMode);
        AssertEx.Equal((byte)127, layer.Opacity);
        AssertEx.False(layer.IsVisible);
        AssertEx.Equal(0, layer.Left);
        AssertEx.Equal(0, layer.Top);
        AssertEx.Equal(2, layer.Width);
        AssertEx.Equal(2, layer.Height);
        AssertEx.True(layer.Bgra != null, "Expected decoded layer pixels.");
        AssertEx.SequenceEqual(new byte[] { 10, 20, 30, 40 }, layer.Bgra!.Take(4));
        AssertEx.SequenceEqual(new byte[] { 50, 60, 70, 80 }, layer.Bgra!.Skip(4).Take(4));
    }

    public static void Export_WritesValidLayerInfoStructure()
    {
        var data = ExportSamplePsd();
        var r = new PsdBytes(data);

        AssertEx.Equal("8BPS", r.Ascii(4));
        AssertEx.Equal((ushort)1, r.U16());
        r.Skip(6);
        AssertEx.Equal((ushort)4, r.U16());
        AssertEx.Equal(2u, r.U32());
        AssertEx.Equal(2u, r.U32());
        AssertEx.Equal((ushort)8, r.U16());
        AssertEx.Equal((ushort)3, r.U16());

        r.Skip((int)r.U32()); // color mode data
        r.Skip((int)r.U32()); // image resources

        var layerMaskLen = r.U32();
        var layerMaskEnd = checked(r.Position + (int)layerMaskLen);
        AssertEx.True(layerMaskLen > 0);

        var layerInfoLen = r.U32();
        var layerInfoEnd = checked(r.Position + (int)layerInfoLen);
        AssertEx.True(layerInfoLen > 0);
        AssertEx.Equal((short)-1, r.I16());

        AssertEx.Equal(0, r.I32()); // top
        AssertEx.Equal(0, r.I32()); // left
        AssertEx.Equal(2, r.I32()); // bottom
        AssertEx.Equal(2, r.I32()); // right
        AssertEx.Equal((ushort)4, r.U16());

        var channelLengths = new List<uint>();
        AssertEx.Equal((short)-1, r.I16());
        channelLengths.Add(r.U32());
        AssertEx.Equal((short)0, r.I16());
        channelLengths.Add(r.U32());
        AssertEx.Equal((short)1, r.I16());
        channelLengths.Add(r.U32());
        AssertEx.Equal((short)2, r.I16());
        channelLengths.Add(r.U32());
        AssertEx.True(channelLengths.All(length => length > 6));

        AssertEx.Equal("8BIM", r.Ascii(4));
        AssertEx.Equal("mul ", r.Ascii(4));
        AssertEx.Equal((byte)127, r.Byte());
        AssertEx.Equal((byte)0, r.Byte());
        AssertEx.Equal((byte)2, r.Byte());
        AssertEx.Equal((byte)0, r.Byte());

        var extraLen = r.U32();
        var extraEnd = checked(r.Position + (int)extraLen);
        AssertEx.True(extraLen >= 12);
        AssertEx.Equal(0u, r.U32());
        AssertEx.Equal(0u, r.U32());
        var nameLen = r.Byte();
        AssertEx.Equal((byte)3, nameLen);
        AssertEx.Equal("Ink", r.Ascii(nameLen));
        r.Position = extraEnd;

        foreach (var length in channelLengths)
        {
            var channelEnd = checked(r.Position + (int)length);
            AssertEx.Equal((ushort)1, r.U16());
            var row0 = r.U16();
            var row1 = r.U16();
            AssertEx.Equal(length - 6, (uint)(row0 + row1));
            r.Position = channelEnd;
        }

        AssertEx.Equal(layerInfoEnd, r.Position);
        AssertEx.Equal(0u, r.U32());
        AssertEx.Equal(layerMaskEnd, r.Position);

        AssertEx.Equal((ushort)0, r.U16());
        AssertEx.Equal(2 * 2 * 4, data.Length - r.Position);
    }

    public static void Export_PreservesFolderHierarchy()
    {
        var document = new DrawingDocument(2, 2);
        document.AddLayer();
        document.ActiveLayer!.Name = "Background";
        document.ActiveLayer.Pixels.SetPixel(0, 0, 1, 2, 3, 255);
        document.AddLayer();
        document.ActiveLayer.Name = "Ink";
        document.ActiveLayer.Pixels.SetPixel(1, 1, 4, 5, 6, 255);
        document.GroupSelectedLayers([1]);
        document.ActiveLayer.Name = "Folder A";
        document.ActiveLayer.BlendMode = "PassThrough";
        document.ActiveLayer.IsOpen = false;

        using var stream = new MemoryStream();
        PsdExporter.Export(stream, document);
        stream.Position = 0;

        var psd = PsdReader.Read(stream);
        AssertEx.Equal(2, psd.Layers.Count);
        AssertEx.Equal("Background", psd.Layers[0].Name);

        var group = psd.Layers[1] as PsdGroupNode
            ?? throw new InvalidOperationException("Expected exported PSD to preserve the folder.");
        AssertEx.Equal("Folder A", group.Name);
        AssertEx.Equal("pass", group.BlendMode);
        AssertEx.False(group.IsOpen);
        AssertEx.Equal(1, group.Children.Count);

        var child = group.Children[0] as PsdLayerNode
            ?? throw new InvalidOperationException("Expected folder child to remain inside the group.");
        AssertEx.Equal("Ink", child.Name);
        AssertEx.True(child.Bgra != null);
        AssertEx.SequenceEqual(new byte[] { 4, 5, 6, 255 }, child.Bgra!.Skip((1 * child.Width + 1) * 4).Take(4));

        stream.Position = 0;
        var imported = PsdImporter.Load(stream);
        var importedGroup = imported.Layers.Single(layer => layer.IsGroup);
        AssertEx.Equal("Folder A", importedGroup.Name);
        AssertEx.Equal("PassThrough", importedGroup.BlendMode);
        AssertEx.False(importedGroup.IsOpen);
        AssertEx.Equal(1, importedGroup.Children.Count);
        AssertEx.Equal("Ink", importedGroup.Children[0].Name);
        AssertEx.True(ReferenceEquals(importedGroup, importedGroup.Children[0].Parent));
    }

    private static byte[] ExportSamplePsd()
    {
        var document = new DrawingDocument(2, 2);
        document.AddLayer();
        var layer = document.ActiveLayer;
        layer.Name = "Ink";
        layer.Opacity = 0.5;
        layer.BlendMode = "Multiply";
        layer.IsVisible = false;
        layer.Pixels.SetPixel(0, 0, 10, 20, 30, 40);
        layer.Pixels.SetPixel(1, 0, 50, 60, 70, 80);
        layer.Pixels.SetPixel(0, 1, 90, 100, 110, 120);
        layer.Pixels.SetPixel(1, 1, 130, 140, 150, 160);

        using var stream = new MemoryStream();
        PsdExporter.Export(stream, document);
        return stream.ToArray();
    }

    private sealed class PsdBytes(byte[] data)
    {
        public int Position { get; set; }

        public byte Byte() => data[Position++];

        public ushort U16()
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Position, 2));
            Position += 2;
            return value;
        }

        public short I16()
        {
            var value = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(Position, 2));
            Position += 2;
            return value;
        }

        public uint U32()
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(Position, 4));
            Position += 4;
            return value;
        }

        public int I32()
        {
            var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(Position, 4));
            Position += 4;
            return value;
        }

        public string Ascii(int length)
        {
            var value = System.Text.Encoding.ASCII.GetString(data, Position, length);
            Position += length;
            return value;
        }

        public void Skip(int count) => Position += count;
    }
}

internal static class FlossFileFormatTests
{
    public static void RoundTrip_BasicDocument()
    {
        var doc = new DrawingDocument(100, 80);
        doc.AddLayer();
        doc.ActiveLayer!.Pixels.SetPixel(10, 10, 1, 2, 3, 255);
        doc.ActiveLayer.Pixels.SetPixel(50, 40, 4, 5, 6, 128);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        AssertEx.Equal(100, loaded.Width);
        AssertEx.Equal(80, loaded.Height);
        AssertEx.Equal(1, loaded.Layers.Count);
        AssertEx.Equal(0, loaded.ActiveLayerIndex);

        loaded.Layers[0].Pixels.GetPixel(10, 10, out var b1, out var g1, out var r1, out var a1);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b1, g1, r1, a1]);

        loaded.Layers[0].Pixels.GetPixel(50, 40, out var b2, out var g2, out var r2, out var a2);
        AssertEx.SequenceEqual(new byte[] { 4, 5, 6, 128 }, [b2, g2, r2, a2]);
    }

    public static void RoundTrip_PaperColor()
    {
        var doc = new DrawingDocument(64, 64);
        doc.SetPaperColor(Avalonia.Media.Color.FromArgb(255, 247, 244, 237));

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        AssertEx.Equal(247, loaded.PaperColor.R);
        AssertEx.Equal(244, loaded.PaperColor.G);
        AssertEx.Equal(237, loaded.PaperColor.B);
        AssertEx.Equal(255, loaded.PaperColor.A);
    }

    public static void RoundTrip_PaperLayer()
    {
        var doc = new DrawingDocument(64, 64);
        doc.SetPaperColor(Avalonia.Media.Colors.White);
        doc.AddBackgroundLayer();

        AssertEx.True(doc.PaperLayer != null);
        AssertEx.True(doc.Layers[0].IsPaper);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        AssertEx.Equal(1, loaded.Layers.Count);
        AssertEx.True(loaded.Layers[0].IsPaper);
        AssertEx.Equal("Paper", loaded.Layers[0].Name);
        AssertEx.True(loaded.PaperLayer != null);
        AssertEx.True(ReferenceEquals(loaded.PaperLayer, loaded.Layers[0]));
    }

    public static void RoundTrip_OutOfBoundsTiles()
    {
        var doc = new DrawingDocument(64, 64);
        doc.AddLayer();
        // Draw outside the document bounds — creates negative-coordinate tiles
        doc.ActiveLayer!.Pixels.SetPixel(-10, -20, 7, 8, 9, 255);
        doc.ActiveLayer.Pixels.SetPixel(70, 80, 10, 11, 12, 200);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        loaded.Layers[0].Pixels.GetPixel(-10, -20, out var b1, out var g1, out var r1, out var a1);
        AssertEx.SequenceEqual(new byte[] { 7, 8, 9, 255 }, [b1, g1, r1, a1]);

        loaded.Layers[0].Pixels.GetPixel(70, 80, out var b2, out var g2, out var r2, out var a2);
        AssertEx.SequenceEqual(new byte[] { 10, 11, 12, 200 }, [b2, g2, r2, a2]);
    }

    public static void RoundTrip_MultipleLayersWithGroup()
    {
        var doc = new DrawingDocument(100, 100);
        doc.AddLayer();
        doc.AddGroupLayer();

        // Group the first two layers into the group
        doc.GroupSelectedLayers([0, 1]);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        AssertEx.True(loaded.Layers.Count >= 3);
        AssertEx.True(loaded.Layers.Any(l => l.IsGroup));

        var group = loaded.Layers.First(l => l.IsGroup);
        AssertEx.True(group.Children.Count > 0);
    }

    public static void RoundTrip_EmptyLayer()
    {
        var doc = new DrawingDocument(50, 50);
        doc.AddLayer();
        // Layer with no pixel content

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        AssertEx.Equal(50, loaded.Width);
        AssertEx.Equal(50, loaded.Height);
        AssertEx.Equal(1, loaded.Layers.Count);
        AssertEx.Equal(PixelRegion.Empty, loaded.Layers[0].Pixels.ContentTileBounds);
    }
}
