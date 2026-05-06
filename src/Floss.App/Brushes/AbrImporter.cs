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
        var hdr    = new byte[8];
        var lenBuf = new byte[4];
        byte[]? sampData = null;
        byte[]? descData = null;

        while (stream.Position <= stream.Length - 12)
        {
            if (stream.Read(hdr, 0, 8) < 8) break;
            if (hdr[0] != '8' || hdr[1] != 'B' || hdr[2] != 'I' || hdr[3] != 'M') break;

            if (stream.Read(lenBuf, 0, 4) < 4) break;
            var blockLen = (long)(((uint)lenBuf[0] << 24) | ((uint)lenBuf[1] << 16) |
                                  ((uint)lenBuf[2] << 8)  |  lenBuf[3]);

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
                else               descData = buf;
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
            : [];

        System.Console.Error.WriteLine($"[ABR] Parsed {allParams.Count} brush params from desc");
        foreach (var pp in allParams)
        {
            System.Console.Error.WriteLine($"  Params: name='{pp.Name}' type={pp.BrushType} guid={pp.SampledDataGuid ?? "null"} size={(pp.HasDiameter ? pp.Diameter.ToString("F0") : "def")} spac={(pp.HasSpacing ? pp.Spacing.ToString("F1") : "def")} hard={(pp.HasHardness ? pp.Hardness.ToString("F0") : "def")}");
        }

        // Build GUID → params lookup for sampled brushes
        var guidToParams = new Dictionary<string, AbrBrushParams>(
            StringComparer.OrdinalIgnoreCase);
        var unnamedParams = new Queue<AbrBrushParams>();

        foreach (var p in allParams)
        {
            if (!string.IsNullOrEmpty(p.SampledDataGuid))
                guidToParams.TryAdd(p.SampledDataGuid, p);
            else
                unnamedParams.Enqueue(p);
        }

        System.Console.Error.WriteLine($"[ABR] GUID map has {guidToParams.Count} entries, unnamed queue has {unnamedParams.Count}");
        foreach (var kv in guidToParams)
            System.Console.Error.WriteLine($"  GUID map: {kv.Key} -> {kv.Value.Name}");

        ScanV10Samp(sampData, guidToParams, unnamedParams, results, ref errors);
    }

    // ── Desc block parser ─────────────────────────────────────────────────────

    private sealed class AbrBrushParams
    {
        public string Name = "";
        public string BrushType = ""; // "computedBrush" or "sampledBrush"
        public string? SampledDataGuid;

        // Brush tip shape — sampled brushes don't have Hrdn in desc
        public bool HasDiameter; public double Diameter;   // pixels (#Pxl)
        public bool HasHardness;  public double Hardness;  // percent (#Prc)
        public bool HasAngle;     public double Angle;     // degrees (#Ang)
        public bool HasRoundness; public double Roundness; // percent (#Prc)
        public bool HasSpacing;   public double Spacing;   // percent (#Prc)

        // Dynamics per property: bVTy=jitter control type, jitter=random%, Mnm=minimum%
        public VrParams SizeDyn     = new();
        public VrParams AngleDyn    = new();
        public VrParams RoundnessDyn= new();
        public VrParams FlowDyn     = new(); // prVr = flow/opacity-jitter in PS
        public VrParams OpacityDyn  = new(); // opVr
        public VrParams WetDyn      = new(); // wtVr = wet-edges dynamics
        public VrParams MixDyn      = new(); // mxVr = mix/airbrush

        // Scatter
        public bool UseScatter;
        public double ScatterCount  = 1;
        public bool ScatterBothAxes = true;
        public double ScatterDist   = 0;   // scatterDynamics.jitter

        // Color dynamics
        public bool UseColorDynamics;
        public double HueJitter;
        public double SaturationJitter;
        public double BrightnessJitter;
        public double Purity;          // center saturation

        // Tool options
        public bool HasFlow;      public double Flow;        // 0-100 long
        public bool HasSmoothing; public double Smoothing;   // Smoo: 0-100 long
        public bool HasOpacity;   public double Opacity;     // Opct: 0-100 long
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
        var pos = 0;
        int L = desc.Length;

        var markerHits = new System.Collections.Generic.Dictionary<string, int>();
        var nmCount = 0;
        var skipNames = new System.Collections.Generic.List<string>();

        while (pos < L - 4)
        {
            var advanced = false;

            if (MatchesAt(desc, pos, "UntF"u8) && pos + 18 <= L)
            {
                markerHits["UntF"] = markerHits.GetValueOrDefault("UntF", 0) + 1;
                var key = ReadKeyNameBackward(desc, pos);
                var unit = Encoding.ASCII.GetString(desc, pos + 4, 4).TrimEnd('\0', ' ');
                var val = ReadBigEndianDouble(desc, pos + 8);
                SetParam(ref current, key, val);
                pos += 18; advanced = true;
            }
            else if (MatchesAt(desc, pos, "doub"u8) && pos + 12 <= L)
            {
                markerHits["doub"] = markerHits.GetValueOrDefault("doub", 0) + 1;
                var key = ReadKeyNameBackward(desc, pos);
                var val = ReadBigEndianDouble(desc, pos + 4);
                SetParam(ref current, key, val);
                pos += 12; advanced = true;
            }
            else if (MatchesAt(desc, pos, "long"u8) && pos + 8 <= L)
            {
                markerHits["long"] = markerHits.GetValueOrDefault("long", 0) + 1;
                var key = ReadKeyNameBackward(desc, pos);
                var val = (long)((uint)desc[pos+4] << 24 | (uint)desc[pos+5] << 16 |
                                 (uint)desc[pos+6] << 8  | desc[pos+7]);
                SetParam(ref current, key, val);
                pos += 8; advanced = true;
            }
            else if (MatchesAt(desc, pos, "bool"u8) && pos + 5 <= L)
            {
                markerHits["bool"] = markerHits.GetValueOrDefault("bool", 0) + 1;
                var key = ReadKeyNameBackward(desc, pos);
                SetParam(ref current, key, desc[pos + 4] != 0);
                pos += 5; advanced = true;
            }
            else if (MatchesAt(desc, pos, "TEXT"u8) && pos + 8 <= L)
            {
                markerHits["TEXT"] = markerHits.GetValueOrDefault("TEXT", 0) + 1;
                var key = ReadKeyNameBackward(desc, pos);
                if (markerHits["TEXT"] <= 20 || key == "Nm")
                    System.Console.Error.WriteLine("  TEXT marker at " + pos + " key='" + key + "'");
                var charCount = (int)((uint)desc[pos+4] << 24 | (uint)desc[pos+5] << 16 |
                                      (uint)desc[pos+6] << 8  | desc[pos+7]);
                if (charCount > 0 && charCount <= 512 && pos + 8 + charCount * 2 <= L)
                {
                    string val;
                    try { val = Encoding.BigEndianUnicode.GetString(desc, pos + 8, charCount * 2).TrimEnd('\0'); }
                    catch { val = Encoding.ASCII.GetString(desc, pos + 8, charCount * 2).TrimEnd('\0'); }
                    SetParam(ref current, key, val);

                    // "Nm" TEXT entries mark the start of a NEW brush preset
                    if (key is "Nm")
                    {
                        nmCount++;
                        if (!val.StartsWith("Sampled ", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!IsAllDefaults(ref current))
                                results.Add(current);
                            current = new AbrBrushParams { Name = val };
                        }
                        else
                        {
                            skipNames.Add(val);
                        }
                    }
                }
                pos += 8 + charCount * 2; advanced = true;
            }
            else if (MatchesAt(desc, pos, "enum"u8) && pos + 12 <= L)
            {
                var key = ReadKeyNameBackward(desc, pos);
                var enumType = Encoding.ASCII.GetString(desc, pos + 4, 4).TrimEnd('\0', ' ');
                var enumVal  = Encoding.ASCII.GetString(desc, pos + 8, 4).TrimEnd('\0', ' ');
                SetParam(ref current, key, $"{enumType}.{enumVal}");
                pos += 12;
                while (pos < L && desc[pos] == 0) pos++;
                advanced = true;
            }
            else if (MatchesAt(desc, pos, "Objc"u8))
            {
                // Read class name after Objc — skip internal struct prefix
                pos += 4;
                // Skip past any prefix fields (4+4 bytes common) and look for class name
                // The class name is typically a length-prefixed Pascal string
                while (pos < L && desc[pos] == 0) pos++;
                var cnPos = pos;
                // Try: after 8 bytes of prefix, then class name
                if (cnPos + 8 < L && desc[cnPos + 0] == 0 && desc[cnPos + 4] == 0)
                    cnPos += 8; // skip two I32 fields
                if (cnPos < L && desc[cnPos] < 64) // looks like a length byte
                    cnPos++;
                var cn = ReadObjcClassName(desc, ref cnPos);
                pos = cnPos;

                // Track brush type
                if (cn is "computedBrush" or "sampledBrush")
                    current.BrushType = cn;

                advanced = true;
            }
            else
            {
                pos++;
            }
        }

        // Flush last preset
        if (!IsAllDefaults(ref current))
            results.Add(current);

        System.Console.Error.WriteLine("[ABR desc scan] markers hit: UntF=" +
            markerHits.GetValueOrDefault("UntF", 0) + " bool=" +
            markerHits.GetValueOrDefault("bool", 0) + " long=" +
            markerHits.GetValueOrDefault("long", 0) + " TEXT=" +
            markerHits.GetValueOrDefault("TEXT", 0) + " enum=" +
            markerHits.GetValueOrDefault("enum", 0) + " Objc=" +
            markerHits.GetValueOrDefault("Objc", 0) + " doub=" +
            markerHits.GetValueOrDefault("doub", 0));
        System.Console.Error.WriteLine("[ABR desc scan] Nm TEXT count=" + nmCount +
            ", skipped=" + skipNames.Count + " (" + string.Join(',', skipNames.Take(10)) + ")");

        return results;
    }

    private static bool IsAllDefaults(ref AbrBrushParams p)
    {
        return string.IsNullOrEmpty(p.Name) && string.IsNullOrEmpty(p.BrushType) &&
               string.IsNullOrEmpty(p.SampledDataGuid) && !p.HasDiameter && !p.HasHardness &&
               !p.HasAngle && !p.HasRoundness && !p.HasSpacing;
    }

    // Set a parsed value on the current brush params based on the key name.
    private static void SetParam(ref AbrBrushParams p, string key, object val)
    {
        switch (key)
        {
            case "Nm":    if (val is string s && !s.StartsWith("Sampled ", StringComparison.OrdinalIgnoreCase)) p.Name = s; break;
            case "Brsh":  p.BrushType = val is string bs ? bs : ""; break;
            case "Dmtr":  { p.HasDiameter = true; p.Diameter = (double)val; } break;
            case "Hrdn":  { p.HasHardness = true; p.Hardness = (double)val; } break;
            case "Angl":  { p.HasAngle = true; p.Angle = (double)val; } break;
            case "Rndn":  { p.HasRoundness = true; p.Roundness = (double)val; } break;
            case "Spcn":  { p.HasSpacing = true; p.Spacing = (double)val; } break;
            case "sampledData": if (val is string sd) p.SampledDataGuid = sd; break;

            case "Intr":  p.Interpolation = ConvBool(val); break;
            case "flipX": p.FlipX = ConvBool(val); break;
            case "flipY": p.FlipY = ConvBool(val); break;
            case "minimumDiameter":  p.MinimumDiameter = (double)val; break;
            case "minimumRoundness": p.MinimumRoundness = (double)val; break;
            case "tiltScale":        p.TiltScale = (double)val; break;

            case "useScatter":        p.UseScatter = ConvBool(val); break;
            case "Cnt":               p.ScatterCount = (double)val; break;
            case "bothAxes":          p.ScatterBothAxes = ConvBool(val); break;
            case "useColorDynamics":  p.UseColorDynamics = ConvBool(val); break;
            case "H":                 p.HueJitter = (double)val; break;
            case "Strt":              p.SaturationJitter = (double)val; break;
            case "Brgh":              p.BrightnessJitter = (double)val; break;
            case "purity":            p.Purity = (double)val; break;

            case "flow":   { p.HasFlow = true; p.Flow = val is long lv ? (double)lv : p.Flow; } break;
            case "Smoo":   { p.HasSmoothing = true; p.Smoothing = val is long lv ? (double)lv : p.Smoothing; } break;
            case "Opct":   { p.HasOpacity = true; p.Opacity = val is double dv ? dv : (val is long lv2 ? (double)lv2 : p.Opacity); } break;
            case "Md":     if (val is string md) p.BlendMode = md; break;
            case "smoothingValue": p.SmoothingValue = (double)val; break;
            case "ErsB":   p.IsEraser = ConvBool(val) || (val is long l && l == 1); break;

            // Dynamics control values (flat in the desc, not nested)
            case "bVTy":
                // bVTy belongs to the most recently encountered dynamics group
                // (We can't determine which without context, so store on all)
                break;
            case "jitter":
                // Same — context-dependent
                break;
            case "Mnm":
                break;
        }
    }

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
        Dictionary<string, AbrBrushParams> guidToParams,
        Queue<AbrBrushParams> unnamedParams,
        List<BrushAsset> results, ref int errors)
    {
        var pos = 0;
        var brushIndex = 0;

        while (pos + 4 <= samp.Length)
        {
            var brushSize = (samp[pos] << 24) | (samp[pos + 1] << 16) |
                            (samp[pos + 2] << 8)  |  samp[pos + 3];
            pos += 4;

            if (brushSize <= 0 || pos + brushSize > samp.Length) break;

            // Extract GUID from this samp entry for matching
            var entryGuid = TryReadEntryGuid(samp, pos);
            AbrBrushParams? matchedParams = null;

            if (entryGuid != null)
            {
                var found = guidToParams.TryGetValue(entryGuid, out matchedParams);
                System.Console.Error.WriteLine($"  samp entry {brushIndex}: GUID={entryGuid}, matched={found}");
                if (matchedParams != null) System.Console.Error.WriteLine($"    params: name='{matchedParams.Name}' size={(matchedParams.HasDiameter?matchedParams.Diameter.ToString("F0"):"def")}");
            }
            else if (unnamedParams.Count > 0)
            {
                matchedParams = unnamedParams.Dequeue();
            }

            var name = matchedParams?.Name ?? $"Brush {brushIndex + 1}";

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
    }

    private static string? TryReadEntryGuid(byte[] data, int entryStart)
    {
        if (IsV10Guid(data, entryStart))
        {
            return Encoding.ASCII.GetString(data, entryStart + 1, 36);
        }
        return null;
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
                var top   = (samp[ds + 13] << 8) | samp[ds + 14];
                var left  = (samp[ds + 17] << 8) | samp[ds + 18];
                var bot   = (samp[ds + 21] << 8) | samp[ds + 22];
                var right = (samp[ds + 25] << 8) | samp[ds + 26];
                var depth = samp[ds + 280];
                var comp  = samp[ds + 281];

                if (depth == 8 && bot > top && right > left &&
                    (right - left) is >= 1 and <= 5000 &&
                    (bot   - top)  is >= 1 and <= 5000)
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
            var top   = (int)(((uint)samp[px]     << 24) | ((uint)samp[px + 1]  << 16) | ((uint)samp[px + 2]  << 8) | samp[px + 3]);
            var left  = (int)(((uint)samp[px + 4] << 24) | ((uint)samp[px + 5]  << 16) | ((uint)samp[px + 6]  << 8) | samp[px + 7]);
            var bot   = (int)(((uint)samp[px + 8] << 24) | ((uint)samp[px + 9]  << 16) | ((uint)samp[px + 10] << 8) | samp[px + 11]);
            var right = (int)(((uint)samp[px + 12]<< 24) | ((uint)samp[px + 13] << 16) | ((uint)samp[px + 14] << 8) | samp[px + 15]);
            var depth = (samp[px + 16] << 8) | samp[px + 17];
            var comp  = samp[px + 18];

            if (top  >= 0 && left >= 0 && bot > top && right > left &&
                (right - left) is >= 1 and <= 5000 &&
                (bot   - top)  is >= 1 and <= 5000 &&
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
        if (data[pos]      != '$') return false;
        if (data[pos + 9]  != '-') return false;
        if (data[pos + 14] != '-') return false;
        if (data[pos + 19] != '-') return false;
        if (data[pos + 24] != '-') return false;
        if (data[pos + 37] != 0)   return false;

        for (var i = 1;  i <= 8;  i++) if (!IsHexByte(data[pos + i])) return false;
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

        for (var j = 0; j < storedPixels.Length; j++)
            storedPixels[j] = (byte)(255 - storedPixels[j]);

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
        var pngBytes = PixelsToPng(pixels, w, h);
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

    // Create a BrushPreset from ABR parameters. Returns (preset, tipData, shapeData).
    private static void BuildPreset(string cleanName, byte[] pngBytes,
        AbrBrushParams? p, int spacingPct,
        out BrushPreset preset, out BrushTipData tipData, out BrushTipData shapeData)
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
        var kind = BrushKind.Ink;
        if (p?.IsEraser == true) kind = BrushKind.Eraser;

        tipData = new BrushTipData
        {
            Kind = BrushTipStorageKind.EmbeddedPng,
            PngBytes = pngBytes
        };

        shapeData = new BrushTipData
        {
            Kind = BrushTipStorageKind.Procedural,
            Shape = BrushTipShape.Circle,
            AspectRatio = 1.0f
        };

        var circleShape = new ProceduralBrushTip(BrushTipShape.Circle);

        // ── Angle jitter ──────────────────────────────────────────────────
        var angleJitter = 0f;
        if (p?.AngleDyn.Jitter > 0)
            angleJitter = (float)Math.Clamp(p.AngleDyn.Jitter / 100.0, 0.0, 1.0);

        preset = new BrushPreset(cleanName, kind, size, opacity, hardness, spacing,
            Color.Parse("#111111"), angle)
        {
            Dynamics = BuildDynamics(p),
            Tip = tipData.CreateTip(),
            Shape = circleShape,
            Flow = flow,
            Smoothing = smoothing,
            Color = Color.Parse("#111111"),
            BaseAngleSource = DetectAngleSource(p),
            AngleJitter = angleJitter
        };
    }

    // Build BrushDynamics from ABR variation parameters.
    private static BrushDynamics BuildDynamics(AbrBrushParams? p)
    {
        var d = new BrushDynamics();
        if (p == null) return d;

        d.Size     = FromVrParams(p.SizeDyn);
        d.Opacity  = FromVrParams(p.OpacityDyn);
        d.Flow     = FromVrParams(p.FlowDyn);
        d.Hardness = CurveOption.Off();

        // Scatter: map scatterDistance to Scatter dynamics
        if (p.UseScatter && p.ScatterDist > 0)
        {
            var scatterStrength = Math.Clamp(p.ScatterDist / 100.0, 0.0, 1.0);
            d.Scatter = CurveOption.Off(); // keep scatter off by default
            // We set the scatter as a base multiplier — engine reads this from dynamics
            // The scatter jitter from ABR is an absolute distance, not a curve; skip complex mapping.
        }

        // Angle source: checked separately in DetectAngleSource
        d.Rotation = CurveOption.Off();
        d.Spacing = CurveOption.Off();

        return d;
    }

    // Convert a Photoshop dynamics bVTy to a CurveOption.
    private static CurveOption FromVrParams(VrParams vr)
    {
        if (vr.ControlType == 0)
            return CurveOption.Off();

        var opt = CurveOption.Off();
        opt.MinOutput = (float)Math.Clamp(vr.Minimum / 100.0, 0.0, 1.0);
        opt.MaxOutput = 1f;

        switch (vr.ControlType)
        {
            case 2: // Pen Pressure
                opt.IsEnabled = true;
                opt.Sensors.Clear();
                opt.Sensors.Add(new SensorConfig
                {
                    Type = SensorType.Pressure,
                    Curve = CubicCurve.Deserialize("0,0;1,1") ?? new CubicCurve()
                });
                break;
            case 6: // Initial Direction — maps to DirectionOfLine style
                opt.IsEnabled = true;
                opt.Strength = 0.3f; // subdued
                // We don't map this directly; DetectAngleSource handles direction.
                break;
            case 7: // Stroke direction
                opt.IsEnabled = true;
                opt.Sensors.Clear();
                opt.Sensors.Add(new SensorConfig
                {
                    Type = SensorType.Pressure,
                    Curve = CubicCurve.Deserialize("0,0;1,1") ?? new CubicCurve()
                });
                break;
        }

        return opt;
    }

    private static BrushDynamics.AngleSource DetectAngleSource(AbrBrushParams? p)
    {
        if (p == null) return BrushDynamics.AngleSource.None;
        if (p.AngleDyn.ControlType is 6 or 7) return BrushDynamics.AngleSource.DirectionOfLine;
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
        catch { }
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
