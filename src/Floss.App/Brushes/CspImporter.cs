using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Floss.App.Brushes;

public static class CspImporter
{
    public static List<BrushAsset> Import(Stream stream, out string diagnostic)
    {
        var assets = new List<BrushAsset>();
        diagnostic = "";
        var errorCount = 0;

        var tmpPath = Path.GetTempFileName();
        try
        {
            using (var fs = File.OpenWrite(tmpPath))
                stream.CopyTo(fs);

            using var conn = new SqliteConnection($"Data Source={tmpPath}");
            conn.Open();

            var variantMap = ReadVariants(conn);
            var nodeList = ReadBrushNodes(conn);

            foreach (var node in nodeList)
            {
                try
                {
                    if (!variantMap.TryGetValue(node.VariantId, out var variant))
                        continue;

                    var pngs = ExtractPngsForNode(conn, node.NodeId);
                    var tipPng = PickBestTipPng(pngs);

                    var asset = BuildAsset(node.Name, variant, tipPng, node.IconIndex);
                    if (asset != null) assets.Add(asset);
                }
                catch { errorCount++; }
            }

            diagnostic = $"{assets.Count} imported, {errorCount} errors";
        }
        catch (Exception ex)
        {
            diagnostic = $"Error: {ex.Message}";
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }

        return assets;
    }

    // ── Node reading (handles both single-node .sut and multi-node .sutg) ──────

    private sealed record CspBrushNode(int NodeId, string Name, int VariantId, int IconIndex);

    private static List<CspBrushNode> ReadBrushNodes(SqliteConnection conn)
    {
        var nodes = new List<CspBrushNode>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT _PW_ID, NodeName, NodeVariantID, NodeIcon
            FROM Node
            WHERE NodeName IS NOT NULL AND NodeName != ''
            ORDER BY _PW_ID";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(name)) continue;

            nodes.Add(new CspBrushNode(
                NodeId: reader.GetInt32(0),
                Name: name,
                VariantId: ReadIntOr(reader, 2, 0),
                IconIndex: ReadIntOr(reader, 3, 0)
            ));
        }

        if (nodes.Count == 0)
        {
            // Fallback for single .sut — create one node from the first row's data
            var name = ReadSingleNodeName(conn);
            var icon = ReadSingleNodeIcon(conn);
            nodes.Add(new CspBrushNode(0, name, 0, icon));
        }

        return nodes;
    }

    private static string ReadSingleNodeName(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NodeName FROM Node LIMIT 1";
        return cmd.ExecuteScalar()?.ToString() ?? "CSP Brush";
    }

    private static int ReadSingleNodeIcon(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NodeIcon FROM Node LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    // ── Variant reading ────────────────────────────────────────────────────────

    private sealed record CspVariant(
        int VariantId,
        int Opacity,
        int CompositeMode,
        int AntiAlias,
        int BrushHardness,
        double BrushSize,
        double BrushInterval,
        int BrushFlow,
        int BrushThickness,
        double BrushRotation,
        int BrushMixColor,
        int BrushUseWaterColor,
        double BrushBlur,
        int TextureForPlot,
        int TextureDensity,
        int UseDualBrush,
        int BrushRibbon,
        int BrushUseSpray
    );

    private static Dictionary<int, CspVariant> ReadVariants(SqliteConnection conn)
    {
        var map = new Dictionary<int, CspVariant>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT VariantID, Opacity, CompositeMode, AntiAlias,
                   BrushHardness, BrushSize, BrushInterval, BrushFlow,
                   BrushThickness, BrushRotation, BrushMixColor,
                   BrushUseWaterColor, BrushBlur, TextureForPlot,
                   TextureDensity, UseDualBrush, BrushRibbon, BrushUseSpray
            FROM Variant
            ORDER BY VariantID";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var variant = new CspVariant(
                VariantId: reader.GetInt32(0),
                Opacity: ReadIntOr(reader, 1, 100),
                CompositeMode: ReadIntOr(reader, 2, 0),
                AntiAlias: ReadIntOr(reader, 3, 0),
                BrushHardness: ReadIntOr(reader, 4, 100),
                BrushSize: ReadDoubleOr(reader, 5, 40),
                BrushInterval: ReadDoubleOr(reader, 6, 25),
                BrushFlow: ReadIntOr(reader, 7, 100),
                BrushThickness: ReadIntOr(reader, 8, 100),
                BrushRotation: ReadDoubleOr(reader, 9, 0),
                BrushMixColor: ReadIntOr(reader, 10, 0),
                BrushUseWaterColor: ReadIntOr(reader, 11, 0),
                BrushBlur: ReadDoubleOr(reader, 12, 0),
                TextureForPlot: ReadIntOr(reader, 13, 0),
                TextureDensity: ReadIntOr(reader, 14, 50),
                UseDualBrush: ReadIntOr(reader, 15, 0),
                BrushRibbon: ReadIntOr(reader, 16, 0),
                BrushUseSpray: ReadIntOr(reader, 17, 0)
            );
            map[variant.VariantId] = variant;
        }

        return map;
    }

    private static int ReadIntOr(SqliteDataReader reader, int ordinal, int defaultValue)
        => reader.IsDBNull(ordinal) ? defaultValue : reader.GetInt32(ordinal);

    private static double ReadDoubleOr(SqliteDataReader reader, int ordinal, double defaultValue)
        => reader.IsDBNull(ordinal) ? defaultValue : reader.GetDouble(ordinal);

    // ── PNG extraction ──────────────────────────────────────────────────────────

    private static readonly byte[] PngSig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendSig = [(byte)'I', (byte)'E', (byte)'N', (byte)'D'];

    private static List<byte[]> ExtractPngsForNode(SqliteConnection conn, int nodeId)
    {
        var pngs = new List<byte[]>();

        if (!TableExists(conn, "MaterialFile")) return pngs;

        string query;
        // For single .sut (nodeId=0 from fallback), read all material rows.
        // For .sutg, filter by NodeIndexForMaterial.
        if (nodeId > 0 && ColumnExists(conn, "MaterialFile", "NodeIndexForMaterial"))
            query = "SELECT FileData FROM MaterialFile WHERE NodeIndexForMaterial = @nodeId";
        else
            query = "SELECT FileData FROM MaterialFile";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        if (nodeId > 0)
            cmd.Parameters.AddWithValue("@nodeId", nodeId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var data = (byte[])reader.GetValue(0);
            if (data.Length == 0) continue;

            ScanPngs(data, pngs);
        }

        return pngs;
    }

    private static void ScanPngs(byte[] data, List<byte[]> results)
    {
        var pos = 0;
        while (pos < data.Length - 8)
        {
            var idx = IndexOf(data, PngSig, pos);
            if (idx < 0) break;
            pos = idx + 1;

            var iendIdx = IndexOf(data, IendSig, idx + 8);
            if (iendIdx < 0) continue;

            var end = iendIdx + 8;
            if (end > data.Length) continue;

            var pngData = new byte[end - idx];
            Array.Copy(data, idx, pngData, 0, pngData.Length);
            results.Add(pngData);

            pos = end;
        }
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@t";
        cmd.Parameters.AddWithValue("@t", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info(@t) WHERE name = @c";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        for (var i = start; i <= data.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static byte[]? PickBestTipPng(List<byte[]> pngs)
    {
        byte[]? best = null;
        var bestSize = 0;
        foreach (var png in pngs)
        {
            if (png.Length > bestSize)
            {
                bestSize = png.Length;
                best = png;
            }
        }
        return best;
    }

    // ── Asset construction ──────────────────────────────────────────────────────

    private static BrushAsset? BuildAsset(string nodeName, CspVariant v, byte[]? tipPng, int iconIndex)
    {
        var size = Math.Max(1, (int)Math.Round(v.BrushSize));
        var hardness = Math.Clamp(v.BrushHardness / 100.0, 0.01, 1.0);
        var spacing = Math.Clamp(v.BrushInterval / 100.0, 0.01, 1.0);
        var opacity = Math.Clamp(v.Opacity / 100.0, 0.01, 1.0);
        var flow = Math.Clamp(v.BrushFlow / 100.0, 0.01, 1.0);
        var angle = v.BrushRotation;
        var smooth = v.AntiAlias > 0 ? 0.3 : 0.0;
        var thickness = Math.Clamp(v.BrushThickness / 100.0, 0.01, 1.0);
        var colorMix = v.BrushMixColor > 0 || v.BrushUseWaterColor > 0;
        var blur = Math.Clamp(v.BrushBlur / 20.0, 0.0, 1.0);
        var ribbon = v.BrushRibbon > 0;
        var spray = v.BrushUseSpray > 0;
        var grain = Math.Clamp(v.TextureForPlot > 0 ? v.TextureDensity / 100.0 : 0.0, 0.0, 1.0);

        var blendMode = v.CompositeMode switch
        {
            3 => SKBlendMode.DstOut,
            _ => SKBlendMode.SrcOver
        };

        var tipData = new BrushTipData
        {
            Kind = tipPng is { Length: > 0 }
                ? BrushTipStorageKind.EmbeddedPng
                : BrushTipStorageKind.Procedural,
            PngBytes = tipPng ?? [],
            Shape = BrushTipShape.Circle,
            AspectRatio = (float)thickness
        };

        var brushName = BuildBrushName(nodeName, iconIndex, colorMix, ribbon, spray, v.UseDualBrush > 0);

        var preset = new BrushPreset(brushName, size, opacity, hardness, spacing,
            Avalonia.Media.Color.Parse("#111111"), angle)
        {
            Dynamics = new BrushDynamics(),
            Tip = tipData.CreateTip(),
            Shape = null,
            Flow = flow,
            Smoothing = smooth,
            Color = Avalonia.Media.Color.Parse("#111111"),
            AngleJitter = 0f,
            BlendMode = blendMode,
            TipThickness = thickness,
            TipDirection = BrushTipDirection.Horizontal,
            Grain = grain,
            ColorMix = colorMix,
            BlurAmount = blur,
            SmudgeMode = colorMix ? SmudgeMode.Smudge : SmudgeMode.Blend,
            AmountOfPaint = colorMix ? 0.5 : 1.0,
            DensityOfPaint = 1.0,
            ColorStretch = 0.5
        };

        return new BrushAsset
        {
            Preset = preset,
            Tip = tipData,
            ShapeData = null,
            Category = "CSP Import"
        };
    }

    private static string BuildBrushName(string baseName, int icon, bool mix, bool ribbon, bool spray, bool dual)
    {
        var stampName = icon switch
        {
            110 => "Pencil", 111 => "Pen", 112 => "Marker", 113 => "Crayon",
            116 => "Airbrush", 117 => "Watercolor", 118 => "Blur",
            119 => "Finger tip", 120 => "Eraser", 121 => "Paint",
            122 => "Figure", 124 => "Decoration", 127 => "Gradient",
            132 => "Spray", _ => null
        };

        var name = baseName;
        if (stampName != null) name += $" ({stampName})";
        if (dual) name += " [Dual]";
        if (ribbon) name += " (Ribbon)";
        if (spray) name += " (Spray)";
        if (mix) name += " (Mix)";
        return name;
    }
}
