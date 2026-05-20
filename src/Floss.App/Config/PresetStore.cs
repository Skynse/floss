using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Floss.App.Brushes;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Floss.App;

public sealed class PresetStore
{
    private const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly bool _useWal;

    private PresetStore(string path, bool useWal)
    {
        _path = path;
        _useWal = useWal;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EnsureCreated();
    }

    public static PresetStore OpenDefault() => new(AppPaths.PresetsDatabasePath, useWal: true);

    public static PresetStore Open(string path) => new(path, useWal: true);

    public static PresetStore OpenPackage(string path) => new(path, useWal: false);

    public IReadOnlyList<ToolGroup> LoadToolGroups()
    {
        using var connection = OpenConnection();
        var groups = new List<ToolGroup>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, shortcut_json, default_engine, custom_icon, last_active_preset_id, last_active_category_name
                FROM tool_groups
                ORDER BY sort_order, rowid
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(new ToolGroup
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Shortcut = JsonSerializer.Deserialize<Input.KeyBinding>(reader.GetString(2), JsonOpts) ?? Input.KeyBinding.Empty,
                    DefaultEngine = Enum.Parse<ToolPresetEngine>(reader.GetString(3)),
                    CustomIcon = NullableString(reader, 4),
                    LastActivePresetId = NullableString(reader, 5),
                    LastActiveCategoryName = NullableString(reader, 6)
                });
            }
        }

        foreach (var group in groups)
        {
            group.Presets = LoadPresets(connection, group.Id);
            group.Categories = LoadCategories(connection, group.Id);
        }

        return groups;
    }

    public void SaveToolGroups(IReadOnlyList<ToolGroup> groups)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM category_presets");
        Execute(connection, transaction, "DELETE FROM tool_categories");
        Execute(connection, transaction, "DELETE FROM tool_presets");
        Execute(connection, transaction, "DELETE FROM tool_groups");

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tool_groups(id, name, shortcut_json, default_engine, custom_icon, last_active_preset_id, last_active_category_name, sort_order)
                    VALUES ($id, $name, $shortcut, $engine, $icon, $active, $activeCat, $sort)
                    """;
                Add(command, "$id", group.Id);
                Add(command, "$name", group.Name);
                Add(command, "$shortcut", JsonSerializer.Serialize(group.Shortcut, JsonOpts));
                Add(command, "$engine", group.DefaultEngine.ToString());
                Add(command, "$icon", group.CustomIcon);
                Add(command, "$active", group.LastActivePresetId);
                Add(command, "$activeCat", group.LastActiveCategoryName);
                Add(command, "$sort", groupIndex);
                command.ExecuteNonQuery();
            }

            for (var presetIndex = 0; presetIndex < group.Presets.Count; presetIndex++)
            {
                var preset = group.Presets[presetIndex];
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tool_presets(id, group_id, name, engine, input_process, output_process, brush_asset_id, preset_icon, preset_json, sort_order)
                    VALUES ($id, $group, $name, $engine, $input, $output, $brush, $icon, $json, $sort)
                    """;
                Add(command, "$id", preset.Id);
                Add(command, "$group", group.Id);
                Add(command, "$name", preset.Name);
                Add(command, "$engine", preset.Engine.ToString());
                Add(command, "$input", preset.InputProcess.ToString());
                Add(command, "$output", preset.OutputProcess.ToString());
                Add(command, "$brush", preset.BrushId);
                Add(command, "$icon", preset.PresetIcon);
                Add(command, "$json", JsonSerializer.Serialize(preset, JsonOpts));
                Add(command, "$sort", presetIndex);
                command.ExecuteNonQuery();
            }

            for (var categoryIndex = 0; categoryIndex < group.Categories.Count; categoryIndex++)
            {
                var category = group.Categories[categoryIndex];
                using var categoryCommand = connection.CreateCommand();
                categoryCommand.Transaction = transaction;
                categoryCommand.CommandText = """
                    INSERT INTO tool_categories(group_id, name, sort_order, last_active_preset_id)
                    VALUES ($group, $name, $sort, $activePr)
                    """;
                Add(categoryCommand, "$group", group.Id);
                Add(categoryCommand, "$name", category.Name);
                Add(categoryCommand, "$sort", categoryIndex);
                Add(categoryCommand, "$activePr", category.LastActivePresetId);
                categoryCommand.ExecuteNonQuery();

                for (var presetIndex = 0; presetIndex < category.PresetIds.Count; presetIndex++)
                {
                    using var presetCommand = connection.CreateCommand();
                    presetCommand.Transaction = transaction;
                    presetCommand.CommandText = """
                        INSERT INTO category_presets(group_id, category_name, preset_id, sort_order)
                        VALUES ($group, $category, $preset, $sort)
                        """;
                    Add(presetCommand, "$group", group.Id);
                    Add(presetCommand, "$category", category.Name);
                    Add(presetCommand, "$preset", category.PresetIds[presetIndex]);
                    Add(presetCommand, "$sort", presetIndex);
                    presetCommand.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
    }

    public IReadOnlyList<BrushAsset> LoadBrushAssets()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, asset_json FROM brush_assets ORDER BY sort_order, name, rowid";

        var rows = new List<(string Id, string Json)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));

        var assets = new List<BrushAsset>();
        foreach (var (id, json) in rows)
        {
            try
            {
                var doc = JsonSerializer.Deserialize<BrushAssetDocument>(json, JsonOpts);
                if (doc != null) assets.Add(doc.ToAsset(LoadResources(connection, id)));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, $"PresetStore.LoadAll ({id})");
                Console.Error.WriteLine($"[Floss] Failed to deserialize brush preset '{id}': {ex.Message}");
            }
        }
        return assets;
    }

    public void SaveBrushAsset(BrushAsset asset)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveBrushAsset(connection, transaction, asset);
        transaction.Commit();
    }

    public void SaveBrushAssets(IEnumerable<BrushAsset> assets)
    {
        foreach (var asset in assets)
            SaveBrushAsset(asset);
    }

    public void DeleteBrushAsset(string id)
    {
        using var connection = OpenConnection();
        using (var resourceCommand = connection.CreateCommand())
        {
            resourceCommand.CommandText = "DELETE FROM brush_resources WHERE asset_id = $id";
            Add(resourceCommand, "$id", id);
            resourceCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM brush_assets WHERE id = $id";
        Add(command, "$id", id);
        command.ExecuteNonQuery();
    }

    private void SaveBrushAsset(SqliteConnection connection, SqliteTransaction transaction, BrushAsset asset)
    {
        var resources = new List<BrushResourceDocument>();
        var doc = BrushAssetDocument.FromAsset(asset, resources);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO brush_assets(id, name, asset_json, sort_order)
                VALUES ($id, $name, $json, COALESCE((SELECT sort_order FROM brush_assets WHERE id = $id), (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM brush_assets)))
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    asset_json = excluded.asset_json
                """;
            Add(command, "$id", asset.Id);
            Add(command, "$name", asset.Preset.Name);
            Add(command, "$json", JsonSerializer.Serialize(doc, JsonOpts));
            command.ExecuteNonQuery();
        }

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM brush_resources WHERE asset_id = $id";
            Add(deleteCommand, "$id", asset.Id);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var resource in resources)
        {
            using var resourceCommand = connection.CreateCommand();
            resourceCommand.Transaction = transaction;
            resourceCommand.CommandText = """
                INSERT INTO brush_resources(asset_id, resource_id, kind, mime_type, data)
                VALUES ($asset, $resource, $kind, $mime, $data)
                """;
            Add(resourceCommand, "$asset", asset.Id);
            Add(resourceCommand, "$resource", resource.Id);
            Add(resourceCommand, "$kind", resource.Kind);
            Add(resourceCommand, "$mime", resource.MimeType);
            Add(resourceCommand, "$data", resource.Data);
            resourceCommand.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, byte[]> LoadResources(SqliteConnection connection, string assetId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT resource_id, data FROM brush_resources WHERE asset_id = $asset";
        Add(command, "$asset", assetId);

        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            resources[reader.GetString(0)] = (byte[])reader["data"];
        return resources;
    }

    private List<ToolPreset> LoadPresets(SqliteConnection connection, string groupId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preset_json
            FROM tool_presets
            WHERE group_id = $group
            ORDER BY sort_order, rowid
            """;
        Add(command, "$group", groupId);

        var presets = new List<ToolPreset>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var preset = JsonSerializer.Deserialize<ToolPreset>(reader.GetString(0), JsonOpts);
            if (preset != null)
            {
                preset.MigrateFromLegacy();
                presets.Add(preset);
            }
        }
        return presets;
    }

    private List<ToolCategory> LoadCategories(SqliteConnection connection, string groupId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, cp.preset_id, c.last_active_preset_id
            FROM tool_categories c
            LEFT JOIN category_presets cp ON cp.group_id = c.group_id AND cp.category_name = c.name
            WHERE c.group_id = $group
            ORDER BY c.sort_order, c.rowid, cp.sort_order, cp.rowid
            """;
        Add(command, "$group", groupId);

        var categories = new List<ToolCategory>();
        ToolCategory? current = null;
        string? currentName = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!string.Equals(currentName, name, StringComparison.Ordinal))
            {
                currentName = name;
                current = new ToolCategory { Name = name, LastActivePresetId = NullableString(reader, 2) };
                categories.Add(current);
            }

            if (!reader.IsDBNull(1))
                current!.PresetIds.Add(reader.GetString(1));
        }

        return categories;
    }

    private void EnsureCreated()
    {
        using var connection = OpenConnection();
        Execute(connection, null, _useWal ? "PRAGMA journal_mode=WAL" : "PRAGMA journal_mode=DELETE");
        Execute(connection, null, "PRAGMA foreign_keys=ON");
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            )
            """);
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS tool_groups(
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                shortcut_json TEXT NOT NULL,
                default_engine TEXT NOT NULL,
                custom_icon TEXT NULL,
                last_active_preset_id TEXT NULL,
                last_active_category_name TEXT NULL,
                sort_order INTEGER NOT NULL
            )
            """);
        try { Execute(connection, null, "ALTER TABLE tool_groups ADD COLUMN last_active_category_name TEXT NULL"); }
        catch { /* column already exists */ }
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS tool_presets(
                id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NOT NULL,
                name TEXT NOT NULL,
                engine TEXT NOT NULL,
                input_process TEXT NOT NULL,
                output_process TEXT NOT NULL,
                brush_asset_id TEXT NULL,
                preset_icon TEXT NULL,
                preset_json TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                FOREIGN KEY(group_id) REFERENCES tool_groups(id) ON DELETE CASCADE
            )
            """);
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS tool_categories(
                group_id TEXT NOT NULL,
                name TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                last_active_preset_id TEXT NULL,
                PRIMARY KEY(group_id, name),
                FOREIGN KEY(group_id) REFERENCES tool_groups(id) ON DELETE CASCADE
            )
            """);
        try { Execute(connection, null, "ALTER TABLE tool_categories ADD COLUMN last_active_preset_id TEXT NULL"); }
        catch { /* column already exists */ }
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS category_presets(
                group_id TEXT NOT NULL,
                category_name TEXT NOT NULL,
                preset_id TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                PRIMARY KEY(group_id, category_name, preset_id),
                FOREIGN KEY(group_id, category_name) REFERENCES tool_categories(group_id, name) ON DELETE CASCADE
            )
            """);
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS brush_assets(
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                asset_json TEXT NOT NULL,
                sort_order INTEGER NOT NULL
            )
            """);
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS brush_resources(
                asset_id TEXT NOT NULL,
                resource_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                data BLOB NOT NULL,
                PRIMARY KEY(asset_id, resource_id)
            )
            """);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta(key, value)
            VALUES ('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        Add(command, "$version", SchemaVersion.ToString());
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _path };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? NullableString(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);

    private sealed class BrushAssetDocument
    {
        public string Id { get; set; } = "";
        public string? Category { get; set; }
        public BrushPresetDocument Preset { get; set; } = new();
        public BrushTipDocument Tip { get; set; } = new();
        public List<BrushTipDocument> Tips { get; set; } = [];
        public BrushTipDocument? ShapeData { get; set; }

        public static BrushAssetDocument FromAsset(BrushAsset asset, List<BrushResourceDocument> resources)
        {
            var shapeData = asset.ShapeData?.DeepClone();
            if (shapeData == null && asset.Preset.Shape != null)
            {
                shapeData = new BrushTipData
                {
                    Kind = BrushTipStorageKind.Procedural,
                    Shape = asset.Preset.Shape.Shape,
                    AspectRatio = asset.Preset.Shape.AspectRatio
                };
            }

            return new BrushAssetDocument
            {
                Id = asset.Id,
                Category = asset.Category,
                Preset = BrushPresetDocument.FromPreset(asset.Preset),
                Tip = BrushTipDocument.FromTipData(BrushTipData.FromTip(asset.Preset.Tip), $"{asset.Id}:tip", resources),
                Tips = asset.Preset.Tips
                    .Select((tip, i) => BrushTipDocument.FromTipData(tip, $"{asset.Id}:tip-list:{i}", resources))
                    .ToList(),
                ShapeData = shapeData == null ? null : BrushTipDocument.FromTipData(shapeData, $"{asset.Id}:shape", resources)
            };
        }

        public BrushAsset ToAsset(IReadOnlyDictionary<string, byte[]> resources)
        {
            var tip = Tip.ToTipData(resources);
            var shapeData = ShapeData?.ToTipData(resources);
            var asset = new BrushAsset
            {
                Id = Id,
                FilePath = "",
                Category = Category,
                Tip = tip,
                ShapeData = shapeData
            };
            var tips = Tips.Select(t => t.ToTipData(resources)).ToList();
            asset.Preset = Preset.ToPreset(asset.Tip, asset.ShapeData) with { Tips = tips };
            return asset;
        }
    }

    private sealed class BrushResourceDocument
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string MimeType { get; set; } = "";
        public byte[] Data { get; set; } = [];
    }

    private sealed class BrushTipDocument
    {
        public BrushTipStorageKind Kind { get; set; } = BrushTipStorageKind.Procedural;
        public BrushTipShape Shape { get; set; } = BrushTipShape.Circle;
        public float AspectRatio { get; set; } = 1.0f;
        public string? ResourceId { get; set; }
        public BrushTipNodeGraph? NodeGraph { get; set; }

        public static BrushTipDocument FromTipData(BrushTipData tip, string prefix, List<BrushResourceDocument> resources)
        {
            var doc = new BrushTipDocument
            {
                Kind = tip.Kind,
                Shape = tip.Shape,
                AspectRatio = tip.AspectRatio,
                NodeGraph = tip.NodeGraph?.DeepClone()
            };

            if (tip.Kind == BrushTipStorageKind.EmbeddedPng && tip.PngBytes.Length > 0)
            {
                doc.ResourceId = prefix;
                resources.Add(new BrushResourceDocument
                {
                    Id = prefix,
                    Kind = "brush-tip",
                    MimeType = "image/png",
                    Data = tip.PngBytes.ToArray()
                });
            }

            return doc;
        }

        public BrushTipData ToTipData(IReadOnlyDictionary<string, byte[]> resources)
            => Kind switch
            {
                BrushTipStorageKind.EmbeddedPng => new BrushTipData
                {
                    Kind = BrushTipStorageKind.EmbeddedPng,
                    PngBytes = ResourceId != null && resources.TryGetValue(ResourceId, out var bytes)
                        ? bytes.ToArray()
                        : []
                },
                BrushTipStorageKind.NodeGraph => new BrushTipData
                {
                    Kind = BrushTipStorageKind.NodeGraph,
                    NodeGraph = NodeGraph?.DeepClone()
                },
                _ => new BrushTipData
                {
                    Kind = BrushTipStorageKind.Procedural,
                    Shape = Shape,
                    AspectRatio = AspectRatio
                }
            };
    }

    private sealed class BrushPresetDocument
    {
        public string Name { get; set; } = "";
        public double Size { get; set; }
        public double Opacity { get; set; }
        public double Hardness { get; set; }
        public double Spacing { get; set; }
        public uint Color { get; set; }
        public double Angle { get; set; }
        public string DynamicsJson { get; set; } = "";
        public double Flow { get; set; }
        public bool ColorMix { get; set; }
        public double ColorLoad { get; set; }
        public double ColorStretch { get; set; }
        public double BlurAmount { get; set; }
        public SmudgeMode SmudgeMode { get; set; }
        public MixingMode MixingMode { get; set; }
        public double AmountOfPaint { get; set; }
        public double DensityOfPaint { get; set; }
        public double TipDensity { get; set; }
        public double TipThickness { get; set; } = 1.0;
        public BrushTipDirection TipDirection { get; set; } = BrushTipDirection.Horizontal;
        public BrushTipSelectionMode TipSelectionMode { get; set; } = BrushTipSelectionMode.Single;
        public double Grain { get; set; }
        public double Smoothing { get; set; }
        public SKBlendMode BlendMode { get; set; }
        public BrushDynamics.AngleSource BaseAngleSource { get; set; }
        public float AngleJitter { get; set; }
        public bool FlipHorizontal { get; set; }
        public bool FlipVertical { get; set; }
        public List<BrushParameterGraph> ParameterGraphs { get; set; } = [];

        public static BrushPresetDocument FromPreset(BrushPreset preset) => new()
        {
            Name = preset.Name,
            Size = preset.Size,
            Opacity = preset.Opacity,
            Hardness = preset.Hardness,
            Spacing = preset.Spacing,
            Color = preset.Color.ToUInt32(),
            Angle = preset.Angle,
            DynamicsJson = preset.Dynamics.Serialize(),
            Flow = preset.Flow,
            ColorMix = preset.ColorMix,
            ColorLoad = preset.ColorLoad,
            ColorStretch = preset.ColorStretch,
            BlurAmount = preset.BlurAmount,
            SmudgeMode = preset.SmudgeMode,
            MixingMode = preset.MixingMode,
            AmountOfPaint = preset.AmountOfPaint,
            DensityOfPaint = preset.DensityOfPaint,
            TipDensity = preset.TipDensity,
            TipThickness = preset.TipThickness,
            TipDirection = preset.TipDirection,
            TipSelectionMode = preset.TipSelectionMode,
            Grain = preset.Grain,
            Smoothing = preset.Smoothing,
            BlendMode = preset.BlendMode,
            BaseAngleSource = preset.BaseAngleSource,
            AngleJitter = preset.AngleJitter,
            FlipHorizontal = preset.FlipHorizontal,
            FlipVertical = preset.FlipVertical,
            ParameterGraphs = preset.ParameterGraphs.Select(g => g.DeepClone()).ToList()
        };

        public BrushPreset ToPreset(BrushTipData tip, BrushTipData? shapeData)
        {
            var preset = new BrushPreset(Name, Size, Opacity, Hardness, Spacing, Avalonia.Media.Color.FromUInt32(Color), Angle)
            {
                Dynamics = BrushDynamics.Deserialize(DynamicsJson),
                Flow = Flow,
                ColorMix = ColorMix,
                ColorLoad = ColorLoad,
                ColorStretch = ColorStretch,
                BlurAmount = BlurAmount,
                SmudgeMode = SmudgeMode,
                MixingMode = MixingMode,
                AmountOfPaint = AmountOfPaint,
                DensityOfPaint = DensityOfPaint,
                TipDensity = TipDensity,
                TipThickness = TipThickness <= 0 ? 1.0 : TipThickness,
                TipDirection = TipDirection,
                TipSelectionMode = TipSelectionMode,
                Grain = Grain,
                Smoothing = Smoothing,
                BlendMode = BlendMode,
                BaseAngleSource = BaseAngleSource,
                AngleJitter = AngleJitter,
                FlipHorizontal = FlipHorizontal,
                FlipVertical = FlipVertical,
                ParameterGraphs = ParameterGraphs.Select(g => g.DeepClone()).ToList(),
                Tip = tip.CreateTip()
            };

            if (shapeData is { Kind: BrushTipStorageKind.Procedural })
                preset = preset with { Shape = new ProceduralBrushTip(shapeData.Shape, shapeData.AspectRatio) };

            return preset;
        }
    }
}
