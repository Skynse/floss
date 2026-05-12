using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Media;
using SkiaSharp;

namespace Floss.App.Brushes;

public static class AbrImporter
{
    public static List<BrushAsset> Import(Stream stream, out string diagnostic)
    {
        var results = new List<BrushAsset>();
        var r = new AbrReader(stream);

        var version = r.U16();
        var extra = r.U16();

        if (version is 1 or 2)
        {
            var count = (int)extra;
            for (var i = 0; i < count; i++)
            {
                try { TryReadV12(r, version, results); }
                catch { /* skip corrupt brush */ }
            }
            diagnostic = $"v{version}, {count} entries declared";
        }
        else if (version is 6 or 7)
        {
            var subVersion = (int)extra;
            // Sub-version 2 uses the same 8BIM block layout as v10
            if (subVersion == 2)
            {
                var errors = 0;
                try { ReadV10(stream, results, ref errors); }
                catch { errors++; }
                diagnostic = $"v{version} sub{subVersion}, {results.Count} imported, {errors} errors";
            }
            else
            {
                var errors = 0;
                while (stream.Position < stream.Length - 8)
                {
                    try
                    {
                        if (!TryReadV6(r, subVersion, results)) break;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        if (errors > 5) break;
                        _ = ex;
                    }
                }
                diagnostic = $"v{version} sub{subVersion}, {results.Count} imported, {errors} errors";
            }
        }
        else if (version == 10)
        {
            var errors = 0;
            try { ReadV10(stream, results, ref errors); }
            catch { errors++; }
            diagnostic = $"v10 sub{extra}, {results.Count} imported, {errors} errors";
        }
        else
        {
            diagnostic = $"unsupported ABR version {version} (sub {extra})";
        }

        return results;
    }

    // ── v10 ──────────────────────────────────────────────────────────────────

    private static void ReadV10(Stream stream, List<BrushAsset> results, ref int errors)
    {
        var hdr = new byte[8];
        var lenBuf = new byte[4];
        byte[]? sampData = null;
        byte[]? descData = null;

        while (stream.Position <= stream.Length - 12)
        {
            if (stream.Read(hdr, 0, 8) < 8) break;
            if (hdr[0] != '8' || hdr[1] != 'B' || hdr[2] != 'I' || hdr[3] != 'M') break;

            if (stream.Read(lenBuf, 0, 4) < 4) break;
            var blockLen = (long)(((uint)lenBuf[0] << 24) | ((uint)lenBuf[1] << 16) |
                                  ((uint)lenBuf[2] << 8) | lenBuf[3]);

            var blockStart = stream.Position;
            var tag = Encoding.ASCII.GetString(hdr, 4, 4);

            if (blockLen is > 0 and < 200_000_000 && (tag == "samp" || tag == "desc"))
            {
                var buf = new byte[blockLen];
                var n = 0;
                while (n < (int)blockLen)
                {
                    var read = stream.Read(buf, n, (int)blockLen - n);
                    if (read == 0) break;
                    n += read;
                }
                if (tag == "samp") sampData = buf;
                else descData = buf;
            }

            if (stream.CanSeek)
                stream.Seek(blockStart + blockLen, SeekOrigin.Begin);
            else
            {
                var remaining = blockLen - (stream.Position - blockStart);
                var skip = new byte[4096];
                while (remaining > 0)
                {
                    var read = stream.Read(skip, 0, (int)Math.Min(remaining, skip.Length));
                    if (read == 0) break;
                    remaining -= read;
                }
            }
        }

        if (sampData == null) return;

        var allParams = descData != null
            ? ParseDescBrushParams(descData)
            : new List<AbrBrushParams>();

        ScanV10Samp(sampData, allParams, results, ref errors);
    }

    // ── Desc block parser ─────────────────────────────────────────────────────

    private sealed class AbrBrushParams
    {
        public string Name = "";
        public string BrushType = ""; // "computedBrush" or "sampledBrush"
        public string? SampledDataGuid;

        // Brush tip shape — sampled brushes don't have Hrdn in desc
        public bool HasDiameter; public double Diameter;   // pixels (#Pxl)
        public bool HasHardness; public double Hardness;  // percent (#Prc)
        public bool HasAngle; public double Angle;     // degrees (#Ang)
        public bool HasRoundness; public double Roundness; // percent (#Prc)
        public bool HasSpacing; public double Spacing;   // percent (#Prc)

        // Dynamics per property: bVTy=jitter control type, jitter=random%, Mnm=minimum%
        public VrParams SizeDyn = new();
        public VrParams AngleDyn = new();
        public VrParams RoundnessDyn = new();
        public VrParams FlowDyn = new(); // prVr = flow/opacity-jitter in PS
        public VrParams OpacityDyn = new(); // opVr
        public VrParams ScatterDyn = new();
        public VrParams SpacingDyn = new();
        public VrParams WetDyn = new(); // wtVr = wet-edges dynamics
        public VrParams MixDyn = new(); // mxVr = mix/airbrush

        // Scatter
        public bool UseScatter;
        public double ScatterCount = 1;
        public bool ScatterBothAxes = true;
        public double ScatterDist = 0;   // scatterDynamics.jitter

        // Color dynamics
        public bool UseColorDynamics;
        public double HueJitter;
        public double SaturationJitter;
        public double BrightnessJitter;
        public double Purity;          // center saturation

        // Tool options
        public bool HasFlow; public double Flow;        // 0-100 long
        public bool HasSmoothing; public double Smoothing;   // Smoo: 0-100 long
        public bool HasOpacity; public double Opacity;     // Opct: 0-100 long
        public string BlendMode = "Nrml";
        public double SmoothingValue; // doubl from smoothing group

        // Tip dynamics
        public double MinimumDiameter;
        public double MinimumRoundness = 25;
        public double TiltScale = 200;
        public bool Interpolation = true;
        public bool FlipX, FlipY;

        // Eraser flag
        public bool IsEraser;
    }

    internal struct VrParams
    {
        public int ControlType; // bVTy: 0=off, 1=fade, 2=pressure, 3=tilt, 4=wheel, 5=rotation, 6=initialDir, 7=direction
        public double Jitter;   // random jitter %
        public double Minimum;  // minimum value %
    }

    // Walk the desc block and extract parameters for every brush preset.
    // Uses a flat scan approach: look for type markers (UntF, bool, long, etc.),
    // read their values, and look backward for key names.
    // Brush presets are delineated by "Nm  TEXT" entries (the preset name).
    private static List<AbrBrushParams> ParseDescBrushParams(byte[] desc)
    {
        var results = new List<AbrBrushParams>();
        var current = new AbrBrushParams();
        var context = AbrDescContext.None;
        var pos = 0;
        int L = desc.Length;

        while (pos < L - 4)
        {
            if (MatchesAt(desc, pos, "UntF"u8) && pos + 18 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                var val = ReadBigEndianDouble(desc, pos + 8);
                SetParam(ref current, key, val, context);
                pos += 18;
            }
            else if (MatchesAt(desc, pos, "doub"u8) && pos + 12 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                var val = ReadBigEndianDouble(desc, pos + 4);
                SetParam(ref current, key, val, context);
                pos += 12;
            }
            else if (MatchesAt(desc, pos, "long"u8) && pos + 8 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                var val = (long)((uint)desc[pos + 4] << 24 | (uint)desc[pos + 5] << 16 |
                                 (uint)desc[pos + 6] << 8 | desc[pos + 7]);
                SetParam(ref current, key, val, context);
                pos += 8;
            }
            else if (MatchesAt(desc, pos, "bool"u8) && pos + 5 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                SetParam(ref current, key, desc[pos + 4] != 0, context);
                pos += 5;
            }
            else if (MatchesAt(desc, pos, "TEXT"u8) && pos + 8 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                var charCount = (int)((uint)desc[pos + 4] << 24 | (uint)desc[pos + 5] << 16 |
                                      (uint)desc[pos + 6] << 8 | desc[pos + 7]);
                if (charCount > 0 && charCount <= 512 && pos + 8 + charCount * 2 <= L)
                {
                    string val;
                    try { val = Encoding.BigEndianUnicode.GetString(desc, pos + 8, charCount * 2).TrimEnd('\0'); }
                    catch { val = Encoding.ASCII.GetString(desc, pos + 8, charCount * 2).TrimEnd('\0'); }

                    if (key is "Nm")
                    {
                        if (!val.StartsWith("Sampled ", StringComparison.OrdinalIgnoreCase) &&
                            !val.StartsWith("SampledT", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!IsAllDefaults(ref current))
                                results.Add(current);
                            current = new AbrBrushParams();
                            context = AbrDescContext.None;
                            SetParam(ref current, key, val, context);
                        }
                    }
                    else
                    {
                        SetParam(ref current, key, val, context);
                    }
                }
                pos += 8 + charCount * 2;
            }
            else if (MatchesAt(desc, pos, "enum"u8) && pos + 12 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                var enumType = Encoding.ASCII.GetString(desc, pos + 4, 4).TrimEnd('\0', ' ');
                var enumVal = Encoding.ASCII.GetString(desc, pos + 8, 4).TrimEnd('\0', ' ');
                SetParam(ref current, key, $"{enumType}.{enumVal}", context);
                pos += 12;
                while (pos < L && desc[pos] == 0) pos++;
            }
            else if (MatchesAt(desc, pos, "Objc"u8))
            {
                var objcKey = ReadKeyNameBackward(desc, pos);
                pos += 4;
                if (pos + 9 <= L && desc[pos + 0] == 0 && desc[pos + 4] == 0 && desc[pos + 8] < 64)
                    pos += 9;
                else
                    while (pos < L && desc[pos] == 0) pos++;
                var cn = ReadObjcClassName(desc, ref pos);

                if (objcKey is "Brsh" && cn is "computedBrush" or "sampledBrush")
                    current.BrushType = cn;
                context = ContextForObject(objcKey, cn);
            }
            else
            {
                pos++;
            }
        }

        // Flush last preset
        if (!IsAllDefaults(ref current))
            results.Add(current);

        return results;
    }

    private enum AbrDescContext
    {
        None,
        SizeDynamics,
        AngleDynamics,
        RoundnessDynamics,
        OpacityDynamics,
        FlowDynamics,
        ScatterDynamics,
        SpacingDynamics,
        ColorDynamics,
        WetDynamics,
        MixDynamics
    }

    private static AbrDescContext ContextForObject(string key, string className)
    {
        var text = $"{key} {className}".ToLowerInvariant();
        if (text.Contains("size") || text.Contains("szvr") || text.Contains("diameter")) return AbrDescContext.SizeDynamics;
        if (text.Contains("angle") || text.Contains("angl")) return AbrDescContext.AngleDynamics;
        if (text.Contains("round") || text.Contains("rnd")) return AbrDescContext.RoundnessDynamics;
        if (text.Contains("opacity") || text.Contains("opvr") || text.Contains("opct")) return AbrDescContext.OpacityDynamics;
        if (text.Contains("flow") || text.Contains("prvr")) return AbrDescContext.FlowDynamics;
        if (text.Contains("scatter") || text.Contains("sct")) return AbrDescContext.ScatterDynamics;
        if (text.Contains("spacing") || text.Contains("spcn")) return AbrDescContext.SpacingDynamics;
        if (text.Contains("color")) return AbrDescContext.ColorDynamics;
        if (text.Contains("wet") || text.Contains("wtvr")) return AbrDescContext.WetDynamics;
        if (text.Contains("mix") || text.Contains("mxvr")) return AbrDescContext.MixDynamics;
        return AbrDescContext.None;
    }

    private static bool IsAllDefaults(ref AbrBrushParams p)
    {
        return string.IsNullOrEmpty(p.Name) && string.IsNullOrEmpty(p.BrushType) &&
               string.IsNullOrEmpty(p.SampledDataGuid) && !p.HasDiameter && !p.HasHardness &&
               !p.HasAngle && !p.HasRoundness && !p.HasSpacing;
    }

    // Set a parsed value on the current brush params based on the key name.
    private static void SetParam(ref AbrBrushParams p, string key, object val, AbrDescContext context)
    {
        switch (key)
        {
            case "Nm": if (val is string s && !s.StartsWith("Sampled ", StringComparison.OrdinalIgnoreCase)) p.Name = s; break;
            case "Brsh": p.BrushType = val is string bs ? bs : ""; break;
            case "Dmtr": { p.HasDiameter = true; p.Diameter = (double)val; } break;
            case "Hrdn": { p.HasHardness = true; p.Hardness = (double)val; } break;
            case "Angl": { p.HasAngle = true; p.Angle = (double)val; } break;
            case "Rndn": { p.HasRoundness = true; p.Roundness = (double)val; } break;
            case "Spcn": { p.HasSpacing = true; p.Spacing = (double)val; } break;
            case "sampledData": if (val is string sd) p.SampledDataGuid = sd; break;

            case "Intr": p.Interpolation = ConvBool(val); break;
            case "flipX": p.FlipX = ConvBool(val); break;
            case "flipY": p.FlipY = ConvBool(val); break;
            case "minimumDiameter": p.MinimumDiameter = (double)val; break;
            case "minimumRoundness": p.MinimumRoundness = (double)val; break;
            case "tiltScale": p.TiltScale = (double)val; break;

            case "useScatter": p.UseScatter = ConvBool(val); break;
            case "Cnt": p.ScatterCount = (double)val; break;
            case "bothAxes": p.ScatterBothAxes = ConvBool(val); break;
            case "useColorDynamics": p.UseColorDynamics = ConvBool(val); break;
            case "H": p.HueJitter = (double)val; break;
            case "Strt": p.SaturationJitter = (double)val; break;
            case "Brgh": p.BrightnessJitter = (double)val; break;
            case "purity": p.Purity = (double)val; break;

            case "flow": { p.HasFlow = true; p.Flow = val is long lv ? (double)lv : p.Flow; } break;
            case "Smoo": { p.HasSmoothing = true; p.Smoothing = val is long lv ? (double)lv : p.Smoothing; } break;
            case "Opct": { p.HasOpacity = true; p.Opacity = val is double dv ? dv : (val is long lv2 ? (double)lv2 : p.Opacity); } break;
            case "Md": if (val is string md) p.BlendMode = md; break;
            case "smoothingValue": p.SmoothingValue = (double)val; break;
            case "ErsB": p.IsEraser = ConvBool(val) || (val is long l && l == 1); break;

            // Dynamics control values (flat in the desc, not nested)
            case "bVTy":
                ApplyVrParam(ref p, context, vr => { vr.ControlType = (int)Numeric(val); return vr; });
                break;
            case "jitter":
                if (context == AbrDescContext.ScatterDynamics)
                    p.ScatterDist = Numeric(val);
                ApplyVrParam(ref p, context, vr => { vr.Jitter = Numeric(val); return vr; });
                break;
            case "Mnm":
            case "minimum":
                ApplyVrParam(ref p, context, vr => { vr.Minimum = Numeric(val); return vr; });
                break;
        }
    }

    private static void ApplyVrParam(ref AbrBrushParams p, AbrDescContext context, Func<VrParams, VrParams> update)
    {
        var vr = context switch
        {
            AbrDescContext.SizeDynamics => p.SizeDyn,
            AbrDescContext.AngleDynamics => p.AngleDyn,
            AbrDescContext.RoundnessDynamics => p.RoundnessDyn,
            AbrDescContext.OpacityDynamics => p.OpacityDyn,
            AbrDescContext.FlowDynamics => p.FlowDyn,
            AbrDescContext.ScatterDynamics => p.ScatterDyn,
            AbrDescContext.SpacingDynamics => p.SpacingDyn,
            AbrDescContext.WetDynamics => p.WetDyn,
            AbrDescContext.MixDynamics => p.MixDyn,
            _ => default
        };
        vr = update(vr);
        switch (context)
        {
            case AbrDescContext.SizeDynamics: p.SizeDyn = vr; break;
            case AbrDescContext.AngleDynamics: p.AngleDyn = vr; break;
            case AbrDescContext.RoundnessDynamics: p.RoundnessDyn = vr; break;
            case AbrDescContext.OpacityDynamics: p.OpacityDyn = vr; break;
            case AbrDescContext.FlowDynamics: p.FlowDyn = vr; break;
            case AbrDescContext.ScatterDynamics: p.ScatterDyn = vr; break;
            case AbrDescContext.SpacingDynamics: p.SpacingDyn = vr; break;
            case AbrDescContext.WetDynamics: p.WetDyn = vr; break;
            case AbrDescContext.MixDynamics: p.MixDyn = vr; break;
        }
    }

    private static double Numeric(object val) => val switch
    {
        double d => d,
        long l => l,
        int i => i,
        bool b => b ? 1 : 0,
        _ => 0
    };

    private static bool ConvBool(object val) =>
        val is bool b ? b : (val is long l ? l != 0 : false);

    // Look backward from the type marker position to find the preceding key name.
    private static string ReadKeyNameBackward(byte[] desc, int markerPos)
    {
        var end = markerPos;
        // Skip backward past zeros and spaces (padding)
        while (end > 0 && (desc[end - 1] == 0 || desc[end - 1] == 32)) end--;
        var start = end;
        // Find start of printable key name
        while (start > 0 && desc[start - 1] is >= (byte)32 and <= (byte)126) start--;
        if (start >= end) return "";
        return Encoding.ASCII.GetString(desc, start, end - start);
    }

    private static string ReadObjcClassName(byte[] desc, ref int pos)
    {
        int L = desc.Length;
        while (pos < L && desc[pos] == 0) pos++;
        var start = pos;
        while (pos < L && (desc[pos] >= 'a' && desc[pos] <= 'z' ||
                           desc[pos] >= 'A' && desc[pos] <= 'Z' ||
                           desc[pos] >= '0' && desc[pos] <= '9' ||
                           desc[pos] == '_'))
            pos++;
        return start < pos ? Encoding.ASCII.GetString(desc, start, pos - start) : "";
    }

    private static double ReadBigEndianDouble(byte[] data, int offset)
    {
        if (BitConverter.IsLittleEndian)
        {
            var buf = new byte[8];
            buf[0] = data[offset + 7]; buf[1] = data[offset + 6];
            buf[2] = data[offset + 5]; buf[3] = data[offset + 4];
            buf[4] = data[offset + 3]; buf[5] = data[offset + 2];
            buf[6] = data[offset + 1]; buf[7] = data[offset];
            return BitConverter.ToDouble(buf, 0);
        }
        return BitConverter.ToDouble(data, offset);
    }

    // ── Samp block scan ────────────────────────────────────────────────────────

    private static void ScanV10Samp(byte[] samp,
        List<AbrBrushParams> descParams,
        List<BrushAsset> results, ref int errors)
    {
        var pos = 0;
        var brushIndex = 0;

        while (pos + 4 <= samp.Length)
        {
            var brushSize = (samp[pos] << 24) | (samp[pos + 1] << 16) |
                            (samp[pos + 2] << 8) | samp[pos + 3];
            pos += 4;

            if (brushSize <= 0 || pos + brushSize > samp.Length) break;

            // Sequential pairing: desc entry[N] names samp entry[N].
            // GUID matching is skipped because many ABR files have no GUIDs in desc.
            AbrBrushParams? matchedParams = brushIndex < descParams.Count ? descParams[brushIndex] : null;

            var name = matchedParams?.Name;
            if (string.IsNullOrEmpty(name))
                name = $"Brush {brushIndex + 1}";

            try
            {
                var asset = ParseV10BrushEntry(samp, pos, brushSize, name, brushIndex, matchedParams);
                if (asset != null) { results.Add(asset); brushIndex++; }
                else errors++;
            }
            catch { errors++; }

            var aligned = brushSize + ((4 - brushSize % 4) % 4);
            pos += aligned;
        }

        // Any remaining desc entries beyond samp count are computed brushes (no tip image).
        for (var i = brushIndex; i < descParams.Count; i++)
        {
            var p = descParams[i];
            if (string.IsNullOrEmpty(p.Name)) continue;
            var asset = MakeComputedAsset(p);
            if (asset != null)
                results.Add(asset);
        }
    }

    // ── Per-brush entry parsing ────────────────────────────────────────────────

    private static BrushAsset? ParseV10BrushEntry(byte[] samp, int entryStart,
        int entrySize, string name, int brushIndex, AbrBrushParams? p)
    {
        if (IsV10Guid(samp, entryStart))
        {
            var ds = entryStart + 38;
            if (ds + 283 <= entryStart + entrySize)
            {
                var top = (samp[ds + 13] << 8) | samp[ds + 14];
                var left = (samp[ds + 17] << 8) | samp[ds + 18];
                var bot = (samp[ds + 21] << 8) | samp[ds + 22];
                var right = (samp[ds + 25] << 8) | samp[ds + 26];
                var depth = samp[ds + 280];
                var comp = samp[ds + 281];

                if (depth == 8 && bot > top && right > left &&
                    (right - left) is >= 1 and <= 5000 &&
                    (bot - top) is >= 1 and <= 5000)
                {
                    return DecodeBrushPixels(samp, ds + 282, name,
                        topCrop: top, leftCrop: left,
                        renderH: bot, renderW: right, comp, p);
                }
            }
        }

        for (var offset = 0; offset + 19 <= entrySize; offset += 4)
        {
            var px = entryStart + offset;
            var top = (int)(((uint)samp[px] << 24) | ((uint)samp[px + 1] << 16) | ((uint)samp[px + 2] << 8) | samp[px + 3]);
            var left = (int)(((uint)samp[px + 4] << 24) | ((uint)samp[px + 5] << 16) | ((uint)samp[px + 6] << 8) | samp[px + 7]);
            var bot = (int)(((uint)samp[px + 8] << 24) | ((uint)samp[px + 9] << 16) | ((uint)samp[px + 10] << 8) | samp[px + 11]);
            var right = (int)(((uint)samp[px + 12] << 24) | ((uint)samp[px + 13] << 16) | ((uint)samp[px + 14] << 8) | samp[px + 15]);
            var depth = (samp[px + 16] << 8) | samp[px + 17];
            var comp = samp[px + 18];

            if (top >= 0 && left >= 0 && bot > top && right > left &&
                (right - left) is >= 1 and <= 5000 &&
                (bot - top) is >= 1 and <= 5000 &&
                depth is 8 or 16 or 32 && comp is 0 or 1)
            {
                return DecodeBrushPixels(samp, px + 19, name,
                    topCrop: top, leftCrop: left,
                    renderH: bot, renderW: right, comp, p, depth);
            }
        }

        return null;
    }

    private static bool IsV10Guid(byte[] data, int pos)
    {
        if (pos + 38 > data.Length) return false;
        if (data[pos] != '$') return false;
        if (data[pos + 9] != '-') return false;
        if (data[pos + 14] != '-') return false;
        if (data[pos + 19] != '-') return false;
        if (data[pos + 24] != '-') return false;
        if (data[pos + 37] != 0) return false;

        for (var i = 1; i <= 8; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 10; i <= 13; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 15; i <= 18; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 20; i <= 23; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 25; i <= 36; i++) if (!IsHexByte(data[pos + i])) return false;
        return true;
    }

    private static bool IsHexByte(byte b) =>
        (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    // ── Pixel decoding ────────────────────────────────────────────────────────

    private static BrushAsset? DecodeBrushPixels(
        byte[] data, int pixelOff,
        string name,
        int topCrop, int leftCrop,
        int renderH, int renderW,
        int comp, AbrBrushParams? p = null, int depth = 8)
    {
        if (depth != 8) return null;
        if (renderW <= 0 || renderH <= 0 || renderW > 5000 || renderH > 5000) return null;

        var hActual = renderH - topCrop;
        var wActual = renderW - leftCrop;
        if (hActual <= 0 || wActual <= 0) return null;

        var storedPixels = new byte[hActual * wActual];

        if (comp == 0)
        {
            var needed = hActual * wActual;
            if (pixelOff + needed > data.Length) return null;
            Array.Copy(data, pixelOff, storedPixels, 0, needed);
        }
        else if (comp == 1)
        {
            var rcBase = pixelOff;
            var rdBase = rcBase + hActual * 2;
            if (rdBase > data.Length) return null;

            var rpos = rdBase;
            for (var y = 0; y < hActual; y++)
            {
                var rowCount = (data[rcBase + y * 2] << 8) | data[rcBase + y * 2 + 1];
                if (rpos + rowCount > data.Length) return null;
                var rowSrc = data.AsSpan(rpos, rowCount).ToArray();
                UnpackBitsRow(rowSrc, storedPixels, y * wActual, wActual, 8);
                rpos += rowCount;
            }
        }
        else return null;

        byte[] fullPixels;
        if (topCrop == 0 && leftCrop == 0)
        {
            fullPixels = storedPixels;
        }
        else
        {
            fullPixels = new byte[renderH * renderW];
            for (var y = 0; y < hActual; y++)
            {
                var dstOff = (topCrop + y) * renderW + leftCrop;
                if (dstOff + wActual > fullPixels.Length) break;
                Array.Copy(storedPixels, y * wActual, fullPixels, dstOff, wActual);
            }
        }

        return MakeAsset(name, fullPixels, renderW, renderH, p);
    }

    // ── v1 / v2 ──────────────────────────────────────────────────────────────

    private static void TryReadV12(AbrReader r, int version, List<BrushAsset> results)
    {
        var brushType = r.U16();
        var dataLength = (int)r.U32();
        var blockStart = r.Position;

        try
        {
            if (brushType != 2) return;

            r.Skip(4);
            var spacing = r.U16();

            string name;
            if (version == 1)
            {
                var len = r.Byte();
                var chars = new byte[len];
                r.ReadExact(chars);
                name = Encoding.ASCII.GetString(chars);
            }
            else
            {
                var charCount = r.U16();
                var charBytes = new byte[charCount * 2];
                r.ReadExact(charBytes);
                name = Encoding.BigEndianUnicode.GetString(charBytes).TrimEnd('\0');
            }

            r.Skip(1);
            var top = r.I16();
            var left = r.I16();
            var bottom = r.I16();
            var right = r.I16();
            var w = right - left;
            var h = bottom - top;
            var depth = r.U16();
            var comp = r.Byte();

            if (w <= 0 || h <= 0 || w > 5000 || h > 5000) return;

            var pixels = ReadPixels(r, w, h, depth, comp);
            if (pixels == null) return;

            results.Add(MakeAsset(name, pixels, w, h, null, spacing));
        }
        finally
        {
            var remaining = dataLength - (int)(r.Position - blockStart);
            if (remaining > 0) r.Skip(remaining);
        }
    }

    // ── v6 / v7 ──────────────────────────────────────────────────────────────

    private static bool TryReadV6(AbrReader r, int subVersion, List<BrushAsset> results)
    {
        var brushType = r.I32();
        var blockSize = r.I32();
        if (blockSize <= 0) return false;

        var blockStart = r.Position;

        try
        {
            if (brushType != 2) return true;

            r.Skip(10);
            var spacing = r.U16();

            string name;
            if (subVersion == 1)
            {
                var charCount = r.U16();
                var charBytes = new byte[charCount * 2];
                r.ReadExact(charBytes);
                name = Encoding.BigEndianUnicode.GetString(charBytes).TrimEnd('\0');
            }
            else
            {
                r.Skip(4);
                name = "Brush";
            }

            r.Skip(1);
            var top = r.I16();
            var left = r.I16();
            var bottom = r.I16();
            var right = r.I16();
            var w = right - left;
            var h = bottom - top;
            var depth = r.U16();
            var comp = r.Byte();

            if (w > 0 && h > 0 && w <= 5000 && h <= 5000)
            {
                var pixels = ReadPixels(r, w, h, depth, comp);
                if (pixels != null)
                    results.Add(MakeAsset(name, pixels, w, h, null, spacing));
            }
        }
        finally
        {
            var remaining = blockSize - (int)(r.Position - blockStart);
            if (remaining > 0) r.Skip(remaining);
        }

        return true;
    }

    // ── Pixel decoding ────────────────────────────────────────────────────────

    private static byte[]? ReadPixels(AbrReader r, int w, int h, int depth, int comp)
    {
        if (depth != 8 && depth != 16) return null;

        var pixels = new byte[w * h];

        if (comp == 0)
        {
            if (depth == 8)
            {
                r.ReadExact(pixels);
            }
            else
            {
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = r.Byte();
                    r.Skip(1);
                }
            }
        }
        else if (comp == 1)
        {
            var rowCounts = new int[h];
            for (var y = 0; y < h; y++)
                rowCounts[y] = r.U16();

            for (var y = 0; y < h; y++)
            {
                var rowData = new byte[rowCounts[y]];
                r.ReadExact(rowData);
                UnpackBitsRow(rowData, pixels, y * w, w, depth);
            }
        }
        else return null;

        return pixels;
    }

    private static void UnpackBitsRow(byte[] src, byte[] dst, int dstOffset, int w, int depth)
    {
        var bytesPerPixel = depth / 8;
        var expectedBytes = w * bytesPerPixel;
        Span<byte> row = stackalloc byte[Math.Min(expectedBytes, 16384)];
        if (expectedBytes > 16384) row = new byte[expectedBytes];

        var outPos = 0;
        var inPos = 0;

        while (inPos < src.Length && outPos < expectedBytes)
        {
            var n = (sbyte)src[inPos++];
            if (n >= 0)
            {
                var count = n + 1;
                var copy = Math.Min(count, expectedBytes - outPos);
                src.AsSpan(inPos, copy).CopyTo(row[outPos..]);
                outPos += copy;
                inPos += count;
            }
            else if (n != -128)
            {
                var count = -n + 1;
                var fill = Math.Min(count, expectedBytes - outPos);
                var val = src[inPos++];
                row.Slice(outPos, fill).Fill(val);
                outPos += fill;
            }
        }

        if (depth == 8)
        {
            row[..w].CopyTo(dst.AsSpan(dstOffset));
        }
        else
        {
            for (var x = 0; x < w; x++)
                dst[dstOffset + x] = row[x * 2];
        }
    }

    private static (byte[] Pixels, int Width, int Height) CleanTipMask(byte[] pixels, int w, int h)
    {
        if (w <= 0 || h <= 0 || pixels.Length < w * h)
            return (pixels, w, h);

        var cleaned = pixels.ToArray();
        var background = EstimateBorderMedian(cleaned, w, h);
        var center = EstimateCenterAverage(cleaned, w, h);

        if (background > center + 4)
        {
            for (var i = 0; i < cleaned.Length; i++)
                cleaned[i] = (byte)(255 - cleaned[i]);
            background = EstimateBorderMedian(cleaned, w, h);
            center = EstimateCenterAverage(cleaned, w, h);
        }

        if (background > 1 && center > background + 4)
        {
            var max = 0;
            for (var i = 0; i < cleaned.Length; i++)
            {
                var value = Math.Max(0, cleaned[i] - background);
                cleaned[i] = (byte)value;
                max = Math.Max(max, value);
            }

            if (max is > 0 and < 255)
            {
                var scale = 255.0 / max;
                for (var i = 0; i < cleaned.Length; i++)
                    cleaned[i] = (byte)Math.Clamp((int)Math.Round(cleaned[i] * scale), 0, 255);
            }
        }

        return TrimMask(cleaned, w, h, threshold: 2, padding: 1);
    }

    private static byte EstimateBorderMedian(byte[] pixels, int w, int h)
    {
        var border = new List<byte>(Math.Max(1, w * 2 + h * 2));
        for (var x = 0; x < w; x++)
        {
            border.Add(pixels[x]);
            border.Add(pixels[(h - 1) * w + x]);
        }
        for (var y = 1; y < h - 1; y++)
        {
            border.Add(pixels[y * w]);
            border.Add(pixels[y * w + w - 1]);
        }

        border.Sort();
        return border[border.Count / 2];
    }

    private static double EstimateCenterAverage(byte[] pixels, int w, int h)
    {
        var left = w / 4;
        var top = h / 4;
        var right = Math.Max(left + 1, w - left);
        var bottom = Math.Max(top + 1, h - top);
        long sum = 0;
        var count = 0;

        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                sum += pixels[y * w + x];
                count++;
            }

        return count == 0 ? 0 : sum / (double)count;
    }

    private static (byte[] Pixels, int Width, int Height) TrimMask(byte[] pixels, int w, int h, byte threshold, int padding)
    {
        var minX = w;
        var minY = h;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (pixels[y * w + x] <= threshold) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

        if (maxX < minX || maxY < minY)
            return (pixels, w, h);

        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(w - 1, maxX + padding);
        maxY = Math.Min(h - 1, maxY + padding);

        var outW = maxX - minX + 1;
        var outH = maxY - minY + 1;
        if (outW == w && outH == h)
            return (pixels, w, h);

        var trimmed = new byte[outW * outH];
        for (var y = 0; y < outH; y++)
            Array.Copy(pixels, (minY + y) * w + minX, trimmed, y * outW, outW);

        return (trimmed, outW, outH);
    }

    // ── PNG construction ──────────────────────────────────────────────────────

    private static unsafe byte[] PixelsToPng(byte[] pixels, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var ptr = (byte*)bmp.GetPixels().ToPointer();
        var rowBytes = bmp.RowBytes;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var alpha = pixels[y * w + x];
                var p = ptr + y * rowBytes + x * 4;
                p[0] = 0;
                p[1] = 0;
                p[2] = 0;
                p[3] = alpha;
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── Asset construction ────────────────────────────────────────────────────

    private static BrushAsset MakeAsset(string name, byte[] pixels, int w, int h,
        AbrBrushParams? p, int spacingPct = 25)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Imported Brush" : name.Trim();
        var cleaned = CleanTipMask(pixels, w, h);
        var pngBytes = PixelsToPng(cleaned.Pixels, cleaned.Width, cleaned.Height);
        SaveTipToLibrary(cleanName, pngBytes);

        BuildPreset(cleanName, pngBytes, p, spacingPct, out var preset, out var tipData, out var shapeData);

        return new BrushAsset
        {
            Id = Guid.NewGuid().ToString("N"),
            Preset = preset,
            Tip = tipData,
            ShapeData = shapeData
        };
    }

    // Create a BrushAsset for a computed brush preset (no tip image).
    private static BrushAsset? MakeComputedAsset(AbrBrushParams p)
    {
        var cleanName = string.IsNullOrWhiteSpace(p.Name) ? "Imported Brush" : p.Name.Trim();
        BuildPreset(cleanName, [], p, 25, out var preset, out var tipData, out var shapeData);
        return new BrushAsset
        {
            Id = Guid.NewGuid().ToString("N"),
            Preset = preset,
            Tip = tipData,
            ShapeData = shapeData
        };
    }

    // Build brush parameters from ABR data into a BrushPreset.
    private static void BuildPreset(string cleanName, byte[] pngBytes,
        AbrBrushParams? p, int spacingPct,
        out BrushPreset preset, out BrushTipData tipData, out BrushTipData? shapeData)
    {
        // ── Size ─────────────────────────────────────────────────────────────
        var size = 40.0;
        if (p?.HasDiameter == true) size = p.Diameter;

        // ── Hardness ─────────────────────────────────────────────────────────
        var hardness = 0.9;
        if (p?.HasHardness == true)
            hardness = Math.Clamp(p.Hardness / 100.0, 0.01, 1.0);
        else if (p is { HasDiameter: true, HasHardness: false })
            hardness = 0.5; // sampled brushes default to medium-hard

        // ── Spacing ──────────────────────────────────────────────────────────
        var spacing = Math.Clamp(spacingPct / 100.0, 0.02, 1.0);
        if (p?.HasSpacing == true)
            spacing = Math.Clamp(p.Spacing / 100.0, 0.01, 1.0);
        else if (pngBytes.Length > 0)
            spacing = 0.08; // sampled ABR tips are usually meant to overlap tightly

        // ── Opacity ──────────────────────────────────────────────────────────
        var opacity = 1.0;
        if (p?.HasOpacity == true)
            opacity = Math.Clamp(p.Opacity / 100.0, 0.01, 1.0);

        // ── Flow ─────────────────────────────────────────────────────────────
        var flow = 1.0;
        if (p?.HasFlow == true)
            flow = Math.Clamp(p.Flow / 100.0, 0.01, 1.0);

        // ── Angle ────────────────────────────────────────────────────────────
        var angle = 0.0;
        if (p?.HasAngle == true)
            angle = p.Angle;

        // ── Smoothing ────────────────────────────────────────────────────────
        var smoothing = 0.3;
        if (p?.HasSmoothing == true)
            smoothing = Math.Clamp(p.Smoothing / 100.0, 0.0, 1.0);
        else if (p != null && p.SmoothingValue > 0)
            smoothing = Math.Clamp(p.SmoothingValue / 100.0, 0.0, 1.0);

        // ── Kind ─────────────────────────────────────────────────────────────
        var blendMode = p?.IsEraser == true ? SkiaSharp.SKBlendMode.DstOut : SkiaSharp.SKBlendMode.SrcOver;

        var tipThickness = 1.0;
        if (p?.HasRoundness == true)
            tipThickness = Math.Clamp(p.Roundness / 100.0, 0.01, 1.0);

        tipData = new BrushTipData
        {
            Kind = pngBytes.Length > 0 ? BrushTipStorageKind.EmbeddedPng : BrushTipStorageKind.Procedural,
            PngBytes = pngBytes.Length > 0 ? pngBytes : [],
            Shape = BrushTipShape.Circle,
            AspectRatio = 1.0f
        };

        shapeData = null;

        // ── Angle jitter ──────────────────────────────────────────────────
        var angleJitter = 0f;
        if (p?.AngleDyn.Jitter > 0)
            angleJitter = (float)Math.Clamp(p.AngleDyn.Jitter / 100.0, 0.0, 1.0);
        else if (p?.RoundnessDyn.Jitter > 0)
            angleJitter = (float)Math.Clamp(p.RoundnessDyn.Jitter / 300.0, 0.0, 0.25);

        // ── Color mixing / wet paint ──────────────────────────────────────────
        var hasWet = p?.WetDyn is { ControlType: > 0 } or { Jitter: > 0 };
        var hasMix = p?.MixDyn is { ControlType: > 0 } or { Jitter: > 0 };
        var colorMix = hasWet || hasMix;
        var amountOfPaint = 1.0;
        var densityOfPaint = 1.0;
        var colorStretch = 0.5;
        var blurAmount = 0.0;
        var smudgeMode = SmudgeMode.Blend;
        if (hasMix)
        {
            smudgeMode = SmudgeMode.Smudge;
            amountOfPaint = Math.Clamp(p!.MixDyn.Jitter / 100.0 * 0.6 + 0.2, 0.0, 1.0);
            densityOfPaint = Math.Clamp(p.MixDyn.Minimum / 100.0, 0.0, 1.0);
            colorStretch = Math.Clamp(p.MixDyn.Jitter / 100.0 * 0.5 + 0.1, 0.0, 1.0);
        }
        if (hasWet)
        {
            blurAmount = Math.Clamp(p!.WetDyn.Jitter / 100.0 * 0.8, 0.0, 1.0);
            if (!hasMix)
            {
                amountOfPaint = Math.Clamp(p.WetDyn.Jitter / 100.0 * 0.5 + 0.3, 0.0, 1.0);
                densityOfPaint = Math.Clamp(p.WetDyn.Minimum / 100.0 * 0.7 + 0.3, 0.0, 1.0);
            }
        }

        preset = new BrushPreset(cleanName, size, opacity, hardness, spacing,
            Color.Parse("#111111"), angle)
        {
            Dynamics = BuildDynamics(p),
            Tip = tipData.CreateTip(),
            Shape = null,
            Flow = flow,
            Smoothing = smoothing,
            Color = Color.Parse("#111111"),
            BaseAngleSource = DetectAngleSource(p),
            AngleJitter = angleJitter,
            BlendMode = blendMode,
            TipThickness = tipThickness,
            TipDirection = BrushTipDirection.Horizontal,
            Grain = p?.UseColorDynamics == true
                ? Math.Clamp((Math.Abs(p.HueJitter) + Math.Abs(p.SaturationJitter) + Math.Abs(p.BrightnessJitter)) / 300.0, 0.0, 1.0)
                : 0.0,
            ColorMix = colorMix,
            AmountOfPaint = amountOfPaint,
            DensityOfPaint = densityOfPaint,
            ColorStretch = colorStretch,
            BlurAmount = blurAmount,
            SmudgeMode = smudgeMode
        };
    }

    // Build BrushDynamics from ABR variation parameters.
    private static BrushDynamics BuildDynamics(AbrBrushParams? p)
    {
        var d = new BrushDynamics();
        if (p == null) return d;

        d.Size = FromVrParams(p.SizeDyn);
        d.Opacity = FromVrParams(p.OpacityDyn);
        d.Flow = FromVrParams(p.FlowDyn);
        d.Hardness = CurveOption.Off();
        d.Spacing = FromVrParams(p.SpacingDyn);

        if (p.UseScatter || p.ScatterDist > 0 || p.ScatterDyn.Jitter > 0)
        {
            var scatterStrength = Math.Clamp(Math.Max(p.ScatterDist, p.ScatterDyn.Jitter) / 100.0, 0.02, 2.0);
            d.Scatter = ConstantOption((float)scatterStrength);
        }

        d.Rotation = RotationOption(p.AngleDyn);

        return d;
    }

    // Convert a Photoshop dynamics bVTy to a CurveOption.
    private static CurveOption FromVrParams(VrParams vr)
    {
        if (vr.ControlType == 0 && vr.Jitter <= 0)
            return CurveOption.Off();

        var jitter = (float)Math.Clamp(vr.Jitter / 100.0, 0.0, 2.0);
        var opt = new CurveOption
        {
            IsEnabled = true,
            MinOutput = (float)Math.Clamp(vr.Minimum / 100.0, 0.0, 1.0),
            MaxOutput = Math.Max(1f, 1f + jitter),
            CombineMode = jitter > 0 && vr.ControlType != 0 ? SensorCombineMode.Add : SensorCombineMode.Multiply
        };

        switch (vr.ControlType)
        {
            case 0:
                opt.MinOutput = Math.Max(0f, 1f - jitter);
                opt.MaxOutput = 1f + jitter;
                opt.Sensors.Add(new SensorConfig { Type = SensorType.Random, Curve = CubicCurve.Identity() });
                break;
            case 1: // Fade
                opt.Sensors.Add(new SensorConfig { Type = SensorType.Fade, Length = 120, Curve = CubicCurve.Identity() });
                break;
            case 2: // Pen Pressure
                opt.Sensors.Add(new SensorConfig
                {
                    Type = SensorType.Pressure,
                    Curve = CubicCurve.Deserialize("0,0;1,1") ?? new CubicCurve()
                });
                break;
            case 3: // Pen Tilt
                opt.Sensors.Add(new SensorConfig { Type = SensorType.TiltX, Curve = CubicCurve.Identity() });
                break;
            case 5: // Rotation / stylus wheel
                opt.Sensors.Add(new SensorConfig { Type = SensorType.Rotation, Curve = CubicCurve.Identity() });
                break;
            case 6:
            case 7:
                opt.Sensors.Add(new SensorConfig { Type = SensorType.DrawingAngle, Curve = CubicCurve.Identity() });
                break;
        }

        if (jitter > 0 && vr.ControlType != 0)
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Random, Curve = CubicCurve.Identity() });

        if (opt.Sensors.Count == 0)
            return CurveOption.Off();

        return opt;
    }

    private static CurveOption ConstantOption(float value)
    {
        var opt = new CurveOption { MinOutput = value, MaxOutput = value };
        opt.Sensors.Add(new SensorConfig { Type = SensorType.Random, Curve = CubicCurve.Identity() });
        return opt;
    }

    private static CurveOption RotationOption(VrParams vr)
    {
        if (vr.Jitter <= 0 && vr.ControlType is not (3 or 5))
            return CurveOption.Off();

        var opt = new CurveOption
        {
            MinOutput = -1f,
            MaxOutput = 1f,
            Strength = (float)Math.Clamp(Math.Max(vr.Jitter, 25) / 100.0, 0.0, 1.0),
            CombineMode = SensorCombineMode.Add
        };
        opt.Sensors.Add(vr.ControlType switch
        {
            3 => new SensorConfig { Type = SensorType.TiltX, Curve = CubicCurve.Identity() },
            5 => new SensorConfig { Type = SensorType.Rotation, Curve = CubicCurve.Identity() },
            _ => new SensorConfig { Type = SensorType.Random, Curve = CubicCurve.Identity() }
        });
        return opt;
    }

    private static BrushDynamics.AngleSource DetectAngleSource(AbrBrushParams? p)
    {
        if (p == null) return BrushDynamics.AngleSource.None;
        if (p.AngleDyn.ControlType is 6 or 7) return BrushDynamics.AngleSource.DirectionOfLine;
        if (p.AngleDyn.ControlType == 3) return BrushDynamics.AngleSource.PenTilt;
        if (p.AngleDyn.ControlType == 5) return BrushDynamics.AngleSource.PenTwist;
        return BrushDynamics.AngleSource.None;
    }

    // ── Utility ────────────────────────────────────────────────────────────────

    private static bool MatchesAt(byte[] data, int pos, ReadOnlySpan<byte> marker)
    {
        if (pos + marker.Length > data.Length) return false;
        return data.AsSpan(pos, marker.Length).SequenceEqual(marker);
    }

    private static int IndexOfBytes(byte[] data, int start, ReadOnlySpan<byte> marker)
    {
        var end = data.Length - marker.Length;
        for (var i = start; i <= end; i++)
            if (data.AsSpan(i, marker.Length).SequenceEqual(marker))
                return i;
        return -1;
    }

    private static void SaveTipToLibrary(string name, byte[] pngBytes)
    {
        try
        {
            var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".png";
            var destPath = Path.Combine(AppPaths.BrushTipsDirectory, safeName);
            if (!File.Exists(destPath))
                File.WriteAllBytes(destPath, pngBytes);
        }
        catch (Exception ex) { CrashLog.Write(ex, "AbrImporter.ExportBrushTip"); }
    }

    // ── Big-endian stream reader ──────────────────────────────────────────────

    private sealed class AbrReader(Stream s)
    {
        private readonly byte[] _buf = new byte[4];

        public long Position => s.Position;

        public byte Byte()
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return (byte)b;
        }

        public short I16()
        {
            ReadExact(2);
            return (short)((_buf[0] << 8) | _buf[1]);
        }

        public ushort U16()
        {
            ReadExact(2);
            return (ushort)((_buf[0] << 8) | _buf[1]);
        }

        public int I32()
        {
            ReadExact(4);
            return (_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3];
        }

        public uint U32()
        {
            ReadExact(4);
            return (uint)((_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3]);
        }

        public void ReadExact(byte[] dst) => ReadExact(dst.AsSpan());

        public void ReadExact(Span<byte> dst)
        {
            var read = 0;
            while (read < dst.Length)
            {
                var n = s.Read(dst[read..]);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
        }

        public void Skip(int bytes)
        {
            if (bytes <= 0) return;
            if (s.CanSeek) { s.Seek(bytes, SeekOrigin.Current); return; }
            Span<byte> tmp = stackalloc byte[Math.Min(bytes, 4096)];
            while (bytes > 0)
            {
                var n = s.Read(tmp[..Math.Min(bytes, tmp.Length)]);
                if (n == 0) throw new EndOfStreamException();
                bytes -= n;
            }
        }

        private void ReadExact(int count) => ReadExact(_buf.AsSpan(0, count));
    }
}
