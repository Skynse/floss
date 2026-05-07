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
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Psd;
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

        ("KeyBinding parses friendly names and modifiers", KeyBindingTests.Parse_HandlesFriendlyNamesAndModifiers),
        ("KeyBinding formats and displays shortcuts", KeyBindingTests.ToStringAndDisplay_ReturnExpectedText),
        ("KeyBinding matches modifier-only shortcuts", KeyBindingTests.Matches_HandlesModifierOnlyBindings),
        ("KeyBinding updates modifier state on key transitions", KeyBindingTests.ModifierHelpers_UpdateModifierFlags),
        ("KeyBinding JSON converter round-trips strings", KeyBindingTests.JsonConverter_RoundTrips),
        ("Brush size adjustment scales smoothly across sizes", BrushSizeAdjustmentTests.ScalesSmoothlyAcrossSizes),
        ("Brush size adjustment clamps boundaries", BrushSizeAdjustmentTests.ClampsBoundaries),

        ("CubicCurve identity and linear evaluation", BrushTests.CubicCurve_EvaluatesIdentityAndLinearCurves),
        ("CubicCurve clamps, sorts, serializes, and clones points", BrushTests.CubicCurve_PointManagementAndSerialization),
        ("SensorConfig maps raw stroke values", BrushTests.SensorConfig_RawValuesAreNormalized),
        ("CurveOption combines sensors and clones deeply", BrushTests.CurveOption_ComputesAndClones),
        ("BrushDynamics serializes and handles invalid JSON", BrushTests.BrushDynamics_SerializesAndFallsBack),
        ("BrushPreset legacy dynamics bridge preserves settings", BrushTests.BrushPreset_LegacyDynamicsBridge),
        ("Brush engine reuses masks during large strokes", BrushTests.BrushEngine_ReusesMasksDuringLargeStrokes),
        ("Brush engine treats color mix as a master switch", BrushTests.BrushEngine_ColorMixOffIgnoresWetPaintFields),
        ("Brush engine does not dispose tip-owned cached masks", BrushTests.BrushEngine_DoesNotDisposeTipOwnedCachedMasks),
        ("Tool controller does not restore stale engine brush state", BrushTests.ToolController_DoesNotOverridePresetBrushState),
        ("Image brush tips preserve source aspect ratio", BrushTests.ImageBrushTip_PreservesAspectRatio),
        ("ABR preset mapping keeps usable brush dynamics", BrushTests.AbrPresetMapping_KeepsDynamics),
        ("ABR mask cleanup inverts dark-on-light stamps", BrushTests.AbrMaskCleanup_InvertsDarkOnLightMasks),
        ("Preset store round-trips tool groups", PresetStoreTests.ToolGroups_RoundTrip),
        ("Default tool groups have categories", PresetStoreTests.ToolGroups_DefaultsHaveCategories),
        ("Tool group sync categorizes brush assets", PresetStoreTests.ToolGroups_SyncCategorizesBrushAssets),
        ("Preset store round-trips brush assets", PresetStoreTests.BrushAssets_RoundTrip),
        ("Preset packages export sub tools with brush assets", PresetStoreTests.Packages_ExportSubTool),
        ("Preset packages export sub tool groups with brush assets", PresetStoreTests.Packages_ExportSubToolGroup),

        ("DrawingDocument starts with one paintable layer", DrawingDocumentTests.Constructor_SetsInitialLayerState),
        ("DrawingDocument adds, selects, duplicates, and deletes layers", DrawingDocumentTests.LayerManagement_WorksForCommonMutations),
        ("DrawingDocument prevents painting locked and group layers", DrawingDocumentTests.CapabilityFlags_RespectLayerState),
        ("DrawingDocument property changes undo and redo", DrawingDocumentTests.LayerPropertyMutations_UndoRedo),
        ("DrawingDocument reference flag participates in undo and duplicate", DrawingDocumentTests.ReferenceLayerFlag_UndoRedoAndDuplicate),
        ("DrawingDocument selection changes undo and redo", DrawingDocumentTests.SelectionMutations_UndoRedo),
        ("DrawingDocument selection inversion keeps renderable mask geometry", DrawingDocumentTests.SelectionInvert_KeepsRenderableMaskGeometry),
        ("DrawingDocument clear active layer records tile history", DrawingDocumentTests.ClearActiveLayer_UndoRedoRestoresPixels),
        ("DrawingDocument move layer validates invalid targets", DrawingDocumentTests.MoveLayer_ValidatesTargetsAndMoves),
        ("DrawingDocument grouping keeps children and can undo", DrawingDocumentTests.GroupSelectedLayers_CreatesGroupAndUndoRestores),
        ("DrawingDocument import lifecycle resets state", DrawingDocumentTests.ImportLifecycle_ReplacesDocumentState),
        ("DrawingLayer clones tile state through duplicate", DrawingDocumentTests.DuplicateActiveLayer_CopiesPixels),
        ("ToolFactory maps eyedropper sampling options", DrawingDocumentTests.ToolFactory_EyedropperOptions),

        ("PSD exporter writes parseable layer records", PsdExporterTests.Export_CanBeReadBack),
        ("PSD exporter aligns layer extra data and channel blocks", PsdExporterTests.Export_WritesValidLayerInfoStructure),
        ("PSD exporter preserves folder hierarchy", PsdExporterTests.Export_PreservesFolderHierarchy)
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
                InputProcess = InputProcessType.BrushStroke,
                OutputProcess = OutputProcessType.DirectDraw,
                BrushId = "brush-asset",
                AlternateInvocation = new AppKeyBinding(Key.I, KeyModifiers.Alt),
                BrushSize = 31,
                BrushOpacity = 0.72,
                BrushFlow = 0.44,
                BrushColorMix = true,
                BrushSmudgeMode = SmudgeMode.Smear,
                BrushAmountOfPaint = 0.33,
                BrushDensityOfPaint = 0.66,
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
            AssertEx.Near(31, groups[0].Presets[0].BrushSize!.Value);
            AssertEx.Equal(SmudgeMode.Smear, groups[0].Presets[0].BrushSmudgeMode!.Value);
            AssertEx.Near(0.33, groups[0].Presets[0].BrushAmountOfPaint!.Value);
            AssertEx.Near(0.66, groups[0].Presets[0].BrushDensityOfPaint!.Value);
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
            var preset = new BrushPreset("Loaded ABR Stamp", BrushKind.Marker, 77, 0.63, 0.38, 0.17, Color.Parse("#123456"), 24)
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
                Grain = 0.37,
                Smoothing = 0.48,
                BlendMode = SKBlendMode.Multiply,
                BaseAngleSource = BrushDynamics.AngleSource.DirectionOfLine,
                AngleJitter = 0.19f,
                Tip = tip.CreateTip(),
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
                AssertEx.Equal(1, reader.GetInt32(2), "Embedded brush tips should be stored as resource BLOBs.");
                AssertEx.Equal(tip.PngBytes.Length, reader.GetInt32(3));
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
            AssertEx.Equal(SmudgeMode.Smear, loaded.Preset.SmudgeMode);
            AssertEx.Equal(MixingMode.Perceptual, loaded.Preset.MixingMode);
            AssertEx.Equal(SKBlendMode.Multiply, loaded.Preset.BlendMode);
            AssertEx.Equal(BrushDynamics.AngleSource.DirectionOfLine, loaded.Preset.BaseAngleSource);
            AssertEx.Near(0.19, loaded.Preset.AngleJitter);
            AssertEx.True(loaded.Preset.Tip is ImageBrushTip);
            AssertEx.True(loaded.Preset.Shape is { Shape: BrushTipShape.Ellipse, AspectRatio: 0.5f });
            AssertEx.True(loaded.Preset.Dynamics.Size.IsEnabled);
            AssertEx.True(loaded.Preset.Dynamics.Rotation.IsEnabled);
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
        var pen = BrushAsset.FromPreset(new BrushPreset("Technical Pen", BrushKind.Ink, 8, 1, 0.9, 0.1, Color.Parse("#000000"), 0));
        var marker = BrushAsset.FromPreset(new BrushPreset("Marker", BrushKind.Marker, 32, 1, 0.5, 0.1, Color.Parse("#000000"), 0));
        var eraser = BrushAsset.FromPreset(new BrushPreset("Eraser", BrushKind.Eraser, 32, 1, 0.5, 0.1, Color.Parse("#000000"), 0));

        config.SyncWithAssets([pen, marker, eraser]);

        var brushGroup = config.Groups[0];
        var eraserGroup = config.Groups[1];
        AssertEx.True(brushGroup.Categories.Any(c => c.Name == "Pens" && c.PresetIds.Count == 1));
        AssertEx.True(brushGroup.Categories.Any(c => c.Name == "Markers" && c.PresetIds.Count == 1));
        AssertEx.True(eraserGroup.Categories.Any(c => c.Name == "Erasers" && c.PresetIds.Count == 1));

        var uncategorized = brushGroup.Presets.First(p => p.BrushId == pen.Id);
        brushGroup.Categories.Clear();
        config.SyncWithAssets([pen]);
        AssertEx.True(brushGroup.Categories.Any(c => c.Name == "Pens" && c.PresetIds.Contains(uncategorized.Id)));
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
                InputProcess = InputProcessType.BrushStroke,
                OutputProcess = OutputProcessType.DirectDraw,
                BrushId = asset.Id,
                BrushSize = 42
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
                    new ToolPreset { Id = "preset-one", Name = "One", InputProcess = InputProcessType.BrushStroke, OutputProcess = OutputProcessType.DirectDraw, BrushId = assetOne.Id },
                    new ToolPreset { Id = "preset-two", Name = "Two", InputProcess = InputProcessType.BrushStroke, OutputProcess = OutputProcessType.DirectDraw, BrushId = assetTwo.Id },
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
            Preset = new BrushPreset(name, BrushKind.Ink, 24, 1, 0.8, 0.1, Color.Parse("#000000"), 0)
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
            }
        };
        var restored = BrushDynamics.Deserialize(dynamics.Serialize());
        AssertEx.Near(dynamics.EvalSize(StrokePoint(pressure: 0.5f)), restored.EvalSize(StrokePoint(pressure: 0.5f)), 0.01);
        AssertEx.Near(72.0, restored.EvalRotationDeg(StrokePoint(random: 0.7f)), 2.0);
        AssertEx.Equal(1.0f, BrushDynamics.Deserialize("{bad json").EvalSize(StrokePoint()));
    }

    public static void BrushPreset_LegacyDynamicsBridge()
    {
        var preset = new BrushPreset("Test", BrushKind.Ink, 10, 1, 1, 1, Colors.Black, 0)
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
        var brush = new BrushPreset("Big", BrushKind.Ink, 160, 1, 0.7, 0.04, Colors.Black, 0)
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
        var brush = new BrushPreset("Dry", BrushKind.Ink, 30, 1, 1, 0.1, Colors.Black, 0)
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

    public static void BrushEngine_DoesNotDisposeTipOwnedCachedMasks()
    {
        var tip = new CachedCountingBrushTip();
        var brush = new BrushPreset("Cached", BrushKind.Ink, 80, 1, 0.7, 0.1, Colors.Black, 0)
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
            Brush = new BrushPreset("Source", BrushKind.Ink, 10, 1, 1, 0.1, Colors.Black, 0)
        };
        var first = new PresetApplyingTool();
        var second = new PresetApplyingTool();
        var controller = new ToolController(context, first);
        var targetPreset = new ToolPreset
        {
            Name = "Smudge",
            InputProcess = InputProcessType.BrushStroke,
            OutputProcess = OutputProcessType.DirectDraw,
            BrushSize = 80,
            BrushColorMix = true,
            BrushSmudgeMode = SmudgeMode.Smear,
            BrushAmountOfPaint = 0.25,
            BrushDensityOfPaint = 0
        };

        controller.SetActiveTool(second, targetPreset);

        AssertEx.Near(80, context.Brush.Size);
        AssertEx.True(context.Brush.ColorMix);
        AssertEx.Equal(SmudgeMode.Smear, context.Brush.SmudgeMode);
        AssertEx.Near(0.25, context.Brush.AmountOfPaint);
        AssertEx.Near(0, context.Brush.DensityOfPaint);
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

    private static CanvasInputSample Sample(double x, double y, long timeMicros)
        => new(x, y, 1, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);

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
        AssertEx.Equal(1, document.Layers.Count);
        AssertEx.Equal("Layer 1", document.ActiveLayer.Name);
        AssertEx.True(document.CanPaintActiveLayer);
        AssertEx.False(document.CanUndo);
        AssertEx.False(document.IsDirty);
    }

    public static void LayerManagement_WorksForCommonMutations()
    {
        var document = new DrawingDocument(4, 4);
        var layersChanged = 0;
        document.LayersChanged += (_, _) => layersChanged++;

        document.AddLayer();
        AssertEx.Equal(2, document.Layers.Count);
        AssertEx.Equal(1, document.ActiveLayerIndex);

        document.SelectLayer(0);
        document.DuplicateActiveLayer();
        AssertEx.Equal(3, document.Layers.Count);
        AssertEx.Equal("Layer 1 copy", document.ActiveLayer.Name);

        document.DeleteActiveLayer();
        AssertEx.Equal(2, document.Layers.Count);
        AssertEx.True(layersChanged >= 3);
    }

    public static void CapabilityFlags_RespectLayerState()
    {
        var document = new DrawingDocument(4, 4);
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
        document.ToggleLayerReference(0);
        AssertEx.True(document.ActiveLayer.IsReference);

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
    }

    public static void ClearActiveLayer_UndoRedoRestoresPixels()
    {
        var document = new DrawingDocument(4, 4);
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

        document.GroupSelectedLayers([0]);
        AssertEx.Equal(4, document.Layers.Count);
        AssertEx.True(document.ActiveLayer.IsGroup);
        AssertEx.Equal(1, document.ActiveLayer.Children.Count);
        AssertEx.Equal(1, document.ActiveLayer.Children[0].IndentLevel);

        document.Undo();
        AssertEx.Equal(4, document.Layers.Count);
        AssertEx.True(document.Layers.Any(layer => layer.IsGroup));

        document.Undo();
        AssertEx.Equal(3, document.Layers.Count);
        AssertEx.False(document.Layers.Any(layer => layer.IsGroup));
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
        document.ActiveLayer.Pixels.SetPixel(2, 2, 1, 2, 3, 255);
        document.DuplicateActiveLayer();

        document.ActiveLayer.Pixels.GetPixel(2, 2, out var b, out var g, out var r, out var a);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b, g, r, a]);
        document.Layers[0].Pixels.SetPixel(2, 2, 0, 0, 0, 0);
        document.ActiveLayer.Pixels.GetPixel(2, 2, out _, out _, out _, out var duplicateAlpha);
        AssertEx.Equal((byte)255, duplicateAlpha);
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
        document.ActiveLayer.Name = "Background";
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
