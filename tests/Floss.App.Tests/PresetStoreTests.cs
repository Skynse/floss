namespace Floss.App.Tests;

public class PresetStoreTests
{
    [Fact]
    public void ToolGroups_RoundTrip()
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
            TestAssertions.Equal(1, groups.Count);
            TestAssertions.Equal("Brush", groups[0].Name);
            TestAssertions.Equal("Ctrl+B", groups[0].Shortcut.ToString());
            TestAssertions.Equal(fillPreset.Id, groups[0].LastActivePresetId);
            TestAssertions.Equal("Portable Ink", groups[0].Presets[0].Name);
            TestAssertions.Equal(OutputProcessType.DirectDraw, groups[0].Presets[0].OutputProcess);
            TestAssertions.Equal("brush-asset", groups[0].Presets[0].BrushId);
            TestAssertions.Near(31, groups[0].Presets[0].BrushOverride!.Size!.Value);
            TestAssertions.Equal(SmudgeMode.Smear, groups[0].Presets[0].BrushOverride!.SmudgeMode!.Value);
            TestAssertions.Near(0.33, groups[0].Presets[0].BrushOverride!.AmountOfPaint!.Value);
            TestAssertions.Near(0.66, groups[0].Presets[0].BrushOverride!.DensityOfPaint!.Value);
            TestAssertions.Equal(FillReferenceMode.ReferenceLayers, groups[0].Presets[1].FillReference);
            TestAssertions.False(groups[0].Presets[1].ContiguousFill);
            TestAssertions.SequenceEqual([fillPreset.Id, brushPreset.Id], groups[0].Categories[0].PresetIds);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    [Fact]
    public void BrushAssets_RoundTrip()
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
                TestAssertions.True(reader.Read());
                TestAssertions.True(reader.GetInt32(0) > 0, "Brush asset JSON should still store preset parameters.");
                TestAssertions.Equal(0, reader.GetInt32(1), "Brush asset JSON should not contain PNG byte payload fields.");
                TestAssertions.Equal(2, reader.GetInt32(2), "Embedded brush tips should be stored as resource BLOBs.");
                TestAssertions.Equal(tip.PngBytes.Length + materialTip.PngBytes.Length, reader.GetInt32(3));
            }

            var loaded = store.LoadBrushAssets().Single();
            TestAssertions.Equal("abr-stamp", loaded.Id);
            TestAssertions.Equal("", loaded.FilePath);
            TestAssertions.Equal(BrushTipStorageKind.EmbeddedPng, loaded.Tip.Kind);
            TestAssertions.Equal(tip.PngBytes.Length, loaded.Tip.PngBytes.Length);
            TestAssertions.Equal(BrushTipShape.Ellipse, loaded.ShapeData!.Shape);
            TestAssertions.Near(77, loaded.Preset.Size);
            TestAssertions.Near(0.42, loaded.Preset.Flow);
            TestAssertions.Near(0.73, loaded.Preset.ColorStretch);
            TestAssertions.Near(0.66, loaded.Preset.TipDensity);
            TestAssertions.Near(0.42, loaded.Preset.TipThickness);
            TestAssertions.Equal(BrushTipDirection.Vertical, loaded.Preset.TipDirection);
            TestAssertions.Equal(BrushTipSelectionMode.Random, loaded.Preset.TipSelectionMode);
            TestAssertions.Equal(SmudgeMode.Smear, loaded.Preset.SmudgeMode);
            TestAssertions.Equal(MixingMode.Perceptual, loaded.Preset.MixingMode);
            TestAssertions.Equal(SKBlendMode.Multiply, loaded.Preset.BlendMode);
            TestAssertions.Equal(BrushDynamics.AngleSource.DirectionOfLine, loaded.Preset.BaseAngleSource);
            TestAssertions.Near(0.19, loaded.Preset.AngleJitter);
            TestAssertions.True(loaded.Preset.FlipHorizontal);
            TestAssertions.True(loaded.Preset.FlipVertical);
            TestAssertions.Equal(1, loaded.Preset.Tips.Count);
            TestAssertions.Equal(materialTip.PngBytes.Length, loaded.Preset.Tips[0].PngBytes.Length);
            TestAssertions.True(loaded.Preset.Tip is NodeBrushTip nodeTip && nodeTip.IsDirectImageSampler,
                "Persisted PNG tips should restore as graph-backed image sampler tips.");
            TestAssertions.True(loaded.Preset.Shape is { Shape: BrushTipShape.Ellipse, AspectRatio: 0.5f });
            TestAssertions.True(loaded.Preset.Dynamics.Size.IsEnabled);
            TestAssertions.True(loaded.Preset.Dynamics.Rotation.IsEnabled);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    [Fact]
    public void BrushAssets_RoundTripNodeBrushTip()
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

            TestAssertions.Equal(BrushTipStorageKind.NodeGraph, loaded.Tip.Kind);
            TestAssertions.True(loaded.Tip.NodeGraph != null);
            TestAssertions.True(loaded.Preset.Tip is NodeBrushTip);
            TestAssertions.Equal("output", loaded.Tip.NodeGraph!.OutputNodeId);
            TestAssertions.Equal(4, loaded.Tip.NodeGraph.Nodes.Count);
            TestAssertions.Equal(1, loaded.Preset.ParameterGraphs.Count);
            TestAssertions.Equal(BrushParameterTarget.Size, loaded.Preset.ParameterGraphs[0].Target);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    [Fact]
    public void ToolGroups_SyncCategorizesBrushAssets()
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
        TestAssertions.True(brushGroup.Categories.Any(c => c.Name == "Pens" && c.PresetIds.Count == 1));
        TestAssertions.True(brushGroup.Categories.Any(c => c.Name == "Markers" && c.PresetIds.Count == 1));
        TestAssertions.True(eraserGroup.Categories.Any(c => c.Name == "Erasers" && c.PresetIds.Count == 1));

        var penPreset = brushGroup.Presets.First(p => p.BrushId == pen.Id);
        brushGroup.Categories.Clear();
        config.SyncWithAssets([pen]);
        TestAssertions.True(brushGroup.Categories.Any(c => c.Name == "Pens" && c.PresetIds.Contains(penPreset.Id)));
    }

    [Fact]
    public void ToolGroups_DefaultsHaveCategories()
    {
        var config = new ToolGroupConfig();
        foreach (var group in config.Groups)
        {
            TestAssertions.True(group.Categories.Count > 0, $"{group.Name} should have a default category.");
            foreach (var preset in group.Presets)
            {
                TestAssertions.True(group.Categories.Any(c => c.PresetIds.Contains(preset.Id)),
                    $"{group.Name}/{preset.Name} should appear in a category.");
            }
        }

        var fill = config.Groups.First(g => g.Name == "Fill");
        TestAssertions.True(fill.Categories.Any(c => c.Name == "Fill" && c.PresetIds.Count == fill.Presets.Count));

        var select = config.Groups.First(g => g.Name == "Select");
        TestAssertions.True(select.Categories.Any(c => c.Name == "Select" && c.PresetIds.Count == select.Presets.Count));
    }

    [Fact]
    public void Packages_ExportSubTool()
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
            TestAssertions.True(new FileInfo(path).Length > 4096, "Package data must be written into the exported file, not only a WAL sidecar.");
            TestAssertions.False(File.Exists(path + "-wal"), "Exported sub-tool package must be a single portable file.");
            TestAssertions.False(File.Exists(path + "-shm"), "Exported sub-tool package must be a single portable file.");

            var store = PresetStore.Open(path);
            var groups = store.LoadToolGroups();
            var assets = store.LoadBrushAssets();
            TestAssertions.Equal(1, groups.Count);
            TestAssertions.Equal(1, groups[0].Presets.Count);
            TestAssertions.Equal("preset-one", groups[0].Presets[0].Id);
            TestAssertions.Equal("asset-one", groups[0].Presets[0].BrushId);
            TestAssertions.Equal(1, groups[0].Categories.Count);
            TestAssertions.SequenceEqual([preset.Id], groups[0].Categories[0].PresetIds);
            TestAssertions.Equal(1, assets.Count);
            TestAssertions.Equal("asset-one", assets[0].Id);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    [Fact]
    public void Packages_ExportSubToolGroup()
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
            TestAssertions.True(new FileInfo(path).Length > 4096, "Package data must be written into the exported file, not only a WAL sidecar.");
            TestAssertions.False(File.Exists(path + "-wal"), "Exported sub-tool group package must be a single portable file.");
            TestAssertions.False(File.Exists(path + "-shm"), "Exported sub-tool group package must be a single portable file.");

            var store = PresetStore.Open(path);
            var groups = store.LoadToolGroups();
            var assets = store.LoadBrushAssets();
            TestAssertions.Equal(1, groups.Count);
            TestAssertions.Equal(3, groups[0].Presets.Count);
            TestAssertions.SequenceEqual(["preset-one", "preset-two", "fill"], groups[0].Categories[0].PresetIds);
            TestAssertions.SequenceEqual(["asset-one", "asset-two"], assets.Select(a => a.Id));
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    [Fact]
    public void BrushFileFormat_RoundTripsMaterialTipState()
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

            TestAssertions.True(loaded.Preset.Tip is NodeBrushTip nodeTip && nodeTip.IsDirectImageSampler,
                "Material PNG tips should restore as graph-backed image sampler tips.");
            TestAssertions.Near(0.57, loaded.Preset.TipDensity);
            TestAssertions.Near(0.33, loaded.Preset.TipThickness);
            TestAssertions.Equal(BrushTipDirection.Vertical, loaded.Preset.TipDirection);
            TestAssertions.Equal(BrushTipSelectionMode.Sequential, loaded.Preset.TipSelectionMode);
            TestAssertions.True(loaded.Preset.FlipHorizontal);
            TestAssertions.True(loaded.Preset.FlipVertical);
            TestAssertions.Equal(1, loaded.Preset.Tips.Count);
            TestAssertions.Equal(material.PngBytes.Length, loaded.Preset.Tips[0].PngBytes.Length);
        }
        finally
        {
            TryDeleteDatabase(path);
        }
    }

    [Fact]
    public void BrushFileFormat_RoundTripsNodeBrushTip()
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

            TestAssertions.Equal(BrushTipStorageKind.NodeGraph, loaded.Tip.Kind);
            TestAssertions.True(loaded.Tip.NodeGraph != null);
            TestAssertions.True(loaded.Preset.Tip is NodeBrushTip);
            TestAssertions.Equal(1, loaded.Preset.Tips.Count);
            TestAssertions.Equal(BrushTipStorageKind.NodeGraph, loaded.Preset.Tips[0].Kind);
            TestAssertions.Equal(loaded.Tip.NodeGraph!.CacheKey(), loaded.Preset.Tips[0].NodeGraph!.CacheKey());
            TestAssertions.Equal(1, loaded.Preset.ParameterGraphs.Count);
            TestAssertions.Equal(BrushParameterTarget.Opacity, loaded.Preset.ParameterGraphs[0].Target);
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

    [Fact]
    public void BrushPresetOverride_RoundTrip()
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

        TestAssertions.Equal("Base", restored.Name, "Name should come from base preset, not captured");
        TestAssertions.Near(42, restored.Size);
        TestAssertions.Near(0.85, restored.Opacity);
        TestAssertions.Near(0.67, restored.Hardness);
        TestAssertions.Near(0.11, restored.Spacing);
        TestAssertions.Near(0.77, restored.Flow);
        TestAssertions.Near(0.22, restored.Grain);
        TestAssertions.Near(0.44, restored.Smoothing);
        TestAssertions.True(restored.ColorMix);
        TestAssertions.Near(0.33, restored.ColorLoad);
        TestAssertions.Near(0.44, restored.ColorStretch);
        TestAssertions.Near(0.12, restored.BlurAmount);
        TestAssertions.Equal(SmudgeMode.Smear, restored.SmudgeMode);
        TestAssertions.Equal(MixingMode.Perceptual, restored.MixingMode);
        TestAssertions.Near(0.55, restored.AmountOfPaint);
        TestAssertions.Near(0.66, restored.DensityOfPaint);
        TestAssertions.Near(0.88, restored.TipDensity);
        TestAssertions.Near(2.5, restored.TipThickness);
        TestAssertions.Equal(BrushTipDirection.Vertical, restored.TipDirection);
        TestAssertions.Equal(SKBlendMode.Multiply, restored.BlendMode);

        // Dynamics should be captured and restored
        TestAssertions.True(restored.Dynamics.Size.IsEnabled);
        TestAssertions.True(restored.Dynamics.Opacity.IsEnabled);
        TestAssertions.True(restored.Dynamics.Rotation.IsEnabled);

        // Tool identity and paint color stay on the base preset/default.
        TestAssertions.Near(33, restored.Angle, 0.0001, "Angle should be captured by ToolPreset");
        TestAssertions.Equal(BrushDynamics.AngleSource.DirectionOfLine, restored.BaseAngleSource, "BaseAngleSource should be captured");
        TestAssertions.Near(0.15, restored.AngleJitter, 0.0001, "AngleJitter should be captured");
        TestAssertions.Equal(Color.Parse("#000000"), restored.Color, "Color should NOT be captured");

        // Tip/shape are runtime brush state now so graph and image sampler edits survive restart without overwriting the asset default.
        TestAssertions.True(restored.Tip is NodeBrushTip nodeTip && nodeTip.IsDirectImageSampler,
            "Captured image tips should restore through graph-backed image samplers.");
        TestAssertions.True(restored.Shape is { Shape: BrushTipShape.Ellipse, AspectRatio: 0.5f });
    }

    [Fact]
    public void BrushPresetOverride_Isolation()
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

        TestAssertions.Near(10, restoredA.Size, 0.0001, "Preset A should keep its own size");
        TestAssertions.Near(0.5, restoredA.Opacity, 0.0001, "Preset A should keep its own opacity");
        TestAssertions.Near(20, restoredB.Size, 0.0001, "Preset B should keep its own size");
        TestAssertions.Near(0.8, restoredB.Opacity, 0.0001, "Preset B should keep its own opacity");
    }

    [Fact]
    public void BrushPresetOverride_IsolatesAngle()
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

        TestAssertions.Near(30, restoredA.Angle, 0.0001, "Preset A should keep its own angle");
        TestAssertions.Equal(BrushDynamics.AngleSource.DirectionOfLine, restoredA.BaseAngleSource);
        TestAssertions.Near(0.1, restoredA.AngleJitter, 0.0001);
        TestAssertions.Near(120, restoredB.Angle, 0.0001, "Preset B should keep its own angle");
        TestAssertions.Equal(BrushDynamics.AngleSource.PenTilt, restoredB.BaseAngleSource);
        TestAssertions.Near(0.4, restoredB.AngleJitter, 0.0001);
    }

    [Fact]
    public void BrushPresetOverride_PreservesTipAndAngle()
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
        TestAssertions.Near(15, restored.Size);
        TestAssertions.Near(0.9, restored.Opacity);

        // Identity still comes from the base; captured angle settings override it.
        TestAssertions.Equal("Default", restored.Name);
        TestAssertions.Near(45, restored.Angle, 0.0001, "Angle should be captured");
        TestAssertions.Equal(BrushDynamics.AngleSource.PenTilt, restored.BaseAngleSource);
        TestAssertions.Near(0.25, restored.AngleJitter, 0.0001);
        TestAssertions.True(restored.Tip is NodeBrushTip, "Tip graph should be captured as runtime brush state");
        TestAssertions.True(restored.Shape is { Shape: BrushTipShape.Rectangle, AspectRatio: 1.5f },
            "Shape should be captured as runtime brush state");
    }

    [Fact]
    public void BrushPresetOverride_ClearBrushOverrides()
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

        TestAssertions.True(preset.BrushOverride is null);
        TestAssertions.True(preset.BrushSize is null);
    }

    [Fact]
    public void BrushPresetOverride_MigratesLegacyFields()
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

        TestAssertions.Near(31, preset.BrushOverride!.Size!.Value);
        TestAssertions.Near(0.72, preset.BrushOverride.Opacity!.Value);
        TestAssertions.Near(45, preset.BrushOverride.Angle!.Value);
        TestAssertions.Equal(BrushDynamics.AngleSource.DirectionOfLine, preset.BrushOverride.BaseAngleSource!.Value);
        TestAssertions.Near(0.2, preset.BrushOverride.AngleJitter!.Value);
        TestAssertions.Equal(BrushShapeOverrideMode.Null, preset.BrushOverride.ShapeOverride);
        TestAssertions.True(preset.BrushSize is null);
    }

    [Fact]
    public void BrushPresetOverride_CapturesNullShape()
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

        TestAssertions.Equal(BrushShapeOverrideMode.Null, preset.BrushOverride!.ShapeOverride, "Captured null shape must be an explicit override");
        TestAssertions.True(restored.Shape is null, "Graph/image brush state should not inherit the base procedural shape");
    }
}

