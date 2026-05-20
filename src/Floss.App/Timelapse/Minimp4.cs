using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Floss.App.Timelapse;

internal static class Mp4Box
{
    public const uint Co64 = 0x636f3634;
    public const uint Stco = 0x7374636f;
    public const uint Ctts = 0x63747473;
    public const uint DinF = 0x64696e66;
    public const uint DreF = 0x64726566;
    public const uint Edts = 0x65647473;
    public const uint Elst = 0x656c7374;
    public const uint Free = 0x66726565;
    public const uint Hdlr = 0x68646c72;
    public const uint Mdia = 0x6d646961;
    public const uint Mdat = 0x6d646174;
    public const uint Mdhd = 0x6d646864;
    public const uint MinF = 0x6d696e66;
    public const uint Moov = 0x6d6f6f76;
    public const uint Mvhd = 0x6d766864;
    public const uint Stsd = 0x73747364;
    public const uint Stsz = 0x7374737a;
    public const uint Stz2 = 0x73747a32;
    public const uint Stbl = 0x7374626c;
    public const uint Stsc = 0x73747363;
    public const uint Smhd = 0x736d6864;
    public const uint Stss = 0x73747373;
    public const uint Stts = 0x73747473;
    public const uint Trak = 0x7472616b;
    public const uint Tkhd = 0x746b6864;
    public const uint Udta = 0x75647461;
    public const uint Vmhd = 0x766d6864;
    public const uint Url = 0x75726c20;
    public const uint Ftyp = 0x66747970;
    public const uint Iods = 0x696f6473;
    public const uint Esds = 0x65736473;
    public const uint Mp4a = 0x6d703461;
    public const uint Mp4s = 0x6d703473;
    public const uint Mp4v = 0x6d703476;
    public const uint Avc1 = 0x61766331;
    public const uint AvcC = 0x61766343;
    public const uint Btrt = 0x62747274;
    public const uint Hev1 = 0x68657631;
    public const uint Hvc1 = 0x68766331;
    public const uint HvcC = 0x68766343;
    public const uint Meta = 0x6d657461;
    public const uint Ilst = 0x696c7374;
    public const uint Ccmt = 0xa9636d74;
    public const uint Data = 0x64617461;
    public const uint Mvex = 0x6d766578;
    public const uint MehD = 0x6d656864;
    public const uint Trex = 0x74726578;
    public const uint Moof = 0x6d6f6f66;
    public const uint Mfhd = 0x6d666864;
    public const uint Traf = 0x74726166;
    public const uint Tfhd = 0x74666864;
    public const uint Tfdt = 0x74666474;
    public const uint Trun = 0x7472756e;
}

public enum TrackMediaKind
{
    Audio,
    Video,
    Private
}

[StructLayout(LayoutKind.Explicit)]
public struct Mp4TrackUnion
{
    [FieldOffset(0)]
    public ushort AudioChannelCount;
    [FieldOffset(0)]
    public int VideoWidth;
    [FieldOffset(4)]
    public int VideoHeight;
}

public struct Mp4TrackInfo
{
    public uint ObjectTypeIndication;
    public byte Language0;
    public byte Language1;
    public byte Language2;
    public TrackMediaKind TrackMediaKind;
    public uint TimeScale;
    public uint DefaultDuration;
    public Mp4TrackUnion U;
}

public static class Mp4ObjectType
{
    public const uint AudioAac = 0x40;
    public const uint Avc = 0x21;
    public const uint Hevc = 0x23;
    public const uint Mjpeg = 0x6C;
}

public static class Mp4Status
{
    public const int Ok = 0;
    public const int BadArguments = -1;
    public const int NoMemory = -2;
    public const int FileWriteError = -3;
    public const int OnlyOneDsiAllowed = -4;
}

public delegate bool Mp4WriteCallback(long offset, ReadOnlySpan<byte> data, object? token);

public static class Mp4SampleKind
{
    public const int Default = 0;
    public const int RandomAccess = 1;
    public const int Continuation = 2;
}

internal struct SampleDescriptor
{
    public ulong Size;
    public ulong Offset;
    public uint Duration;
    public uint FlagRandomAccess;
}

internal sealed class Track
{
    public Mp4TrackInfo Info;
    public List<SampleDescriptor> Samples = [];
    public List<byte> PendingSample = [];
    public List<byte> Vsps = []; // or DSI for audio
    public List<byte> Vpps = [];
    public List<byte> Vvps = [];
}

public sealed class Mp4Muxer : IDisposable
{
    private List<Track> _tracks = [];
    private long _writePos;
    private Mp4WriteCallback _writeCallback = null!;
    private object? _token;
    private string? _textComment;
    private bool _sequentialMode;
    private bool _enableFragmentation;
    private int _fragmentsCount;

    private static readonly byte[] FtypBox =
    [
        0x00, 0x00, 0x00, 0x18,
        (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'m', (byte)'p', (byte)'4', (byte)'2',
        0x00, 0x00, 0x00, 0x00,
        (byte)'m', (byte)'p', (byte)'4', (byte)'2',
        (byte)'i', (byte)'s', (byte)'o', (byte)'m'
    ];

    private Mp4Muxer() { }

    public static Mp4Muxer? Open(bool sequentialMode, bool fragmentation,
        Mp4WriteCallback writeCallback, object? token)
    {
        if (writeCallback(0, FtypBox, token))
            return null;

        var mux = new Mp4Muxer
        {
            _sequentialMode = sequentialMode || fragmentation,
            _enableFragmentation = fragmentation,
            _writeCallback = writeCallback,
            _token = token,
            _writePos = FtypBox.Length
        };

        if (!mux._sequentialMode)
        {
            var filler = new byte[8] { 0, 0, 0, 0, 0, 0, 0, 8 };
            if (mux._writeCallback(mux._writePos, filler, mux._token))
                return null;
            mux._writePos += 16;
        }

        return mux;
    }

    public int AddTrack(Mp4TrackInfo trackInfo)
    {
        var tr = new Track { Info = trackInfo };
        _tracks.Add(tr);

        if (trackInfo.Language0 == 0 && trackInfo.Language1 == 0 && trackInfo.Language2 == 0)
        {
            tr.Info.Language0 = (byte)'u';
            tr.Info.Language1 = (byte)'n';
            tr.Info.Language2 = (byte)'d';
        }

        return _tracks.Count - 1;
    }

    private static bool AppendUnique(List<byte> list, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 2 <= list.Count;)
        {
            int cb = (list[i] << 8) | list[i + 1];
            if (cb == data.Length)
            {
                bool same = true;
                for (int j = 0; j < cb && i + 2 + j < list.Count; j++)
                {
                    if (list[i + 2 + j] != data[j]) { same = false; break; }
                }
                if (same) return true;
            }
            i += 2 + cb;
        }

        list.Add((byte)(data.Length >> 8));
        list.Add((byte)data.Length);
        list.AddRange(data);
        return true;
    }

    public int SetDsi(int trackId, ReadOnlySpan<byte> dsi)
    {
        var tr = _tracks[trackId];
        if (tr.Vsps.Count > 0)
            return Mp4Status.OnlyOneDsiAllowed;
        return AppendUnique(tr.Vsps, dsi) ? Mp4Status.Ok : Mp4Status.NoMemory;
    }

    public int SetVps(int trackId, ReadOnlySpan<byte> vps)
    {
        var tr = _tracks[trackId];
        return AppendUnique(tr.Vvps, vps) ? Mp4Status.Ok : Mp4Status.NoMemory;
    }

    public int SetSps(int trackId, ReadOnlySpan<byte> sps)
    {
        var tr = _tracks[trackId];
        return AppendUnique(tr.Vsps, sps) ? Mp4Status.Ok : Mp4Status.NoMemory;
    }

    public int SetPps(int trackId, ReadOnlySpan<byte> pps)
    {
        var tr = _tracks[trackId];
        return AppendUnique(tr.Vpps, pps) ? Mp4Status.Ok : Mp4Status.NoMemory;
    }

    public int SetTextComment(string comment)
    {
        _textComment = comment;
        return Mp4Status.Ok;
    }

    private uint GetDuration(Track tr)
    {
        uint sum = 0;
        foreach (var s in tr.Samples)
            sum += s.Duration;
        return sum;
    }

    private int WritePendingData(Track tr)
    {
        if (tr.PendingSample.Count > 0 && tr.Samples.Count > 0)
        {
            var size = tr.PendingSample.Count;
            var header = new byte[8];
            BigEndian.Write32(header, 0, (uint)(size + 8));
            BigEndian.Write32(header, 4, Mp4Box.Mdat);
            if (_writeCallback(_writePos, header, _token))
                return Mp4Status.FileWriteError;
            _writePos += 8;

            var lastSample = tr.Samples[^1];
            lastSample.Size = (ulong)size;
            lastSample.Offset = (ulong)_writePos;
            tr.Samples[^1] = lastSample;

            if (_writeCallback(_writePos, CollectionsMarshal.AsSpan(tr.PendingSample), _token))
                return Mp4Status.FileWriteError;
            _writePos += size;
            tr.PendingSample.Clear();
        }
        return Mp4Status.Ok;
    }

    private bool AddSampleDescriptor(Track tr, int dataBytes, int duration, int kind)
    {
        tr.Samples.Add(new SampleDescriptor
        {
            Size = (ulong)dataBytes,
            Offset = (ulong)_writePos,
            Duration = duration != 0 ? (uint)duration : tr.Info.DefaultDuration,
            FlagRandomAccess = (kind == Mp4SampleKind.RandomAccess) ? 1u : 0u
        });
        return true;
    }

    public int PutSample(int trackNum, ReadOnlySpan<byte> data, int duration, int kind)
    {
        var tr = _tracks[trackNum];

        if (_enableFragmentation)
        {
            if (_fragmentsCount == 0)
            {
                var err = FlushIndex();
                if (err != Mp4Status.Ok) return err;
            }
            _fragmentsCount++;

            var err2 = WriteFragmentHeader(trackNum, data.Length, duration, kind);
            if (err2 != Mp4Status.Ok) return err2;
            err2 = WriteMdatBox((uint)(data.Length + 8));
            if (err2 != Mp4Status.Ok) return err2;
            if (_writeCallback(_writePos, data, _token))
                return Mp4Status.FileWriteError;
            _writePos += data.Length;
            return Mp4Status.Ok;
        }

        if (kind != Mp4SampleKind.Continuation)
        {
            if (_sequentialMode)
            {
                var err = WritePendingData(tr);
                if (err != Mp4Status.Ok) return err;
            }
            AddSampleDescriptor(tr, data.Length, duration, kind);
        }
        else if (!_sequentialMode)
        {
            if (tr.Samples.Count == 0)
                return Mp4Status.NoMemory;
            var last = tr.Samples[^1];
            last.Size += (ulong)data.Length;
            tr.Samples[^1] = last;
        }

        if (_sequentialMode)
        {
            tr.PendingSample.AddRange(data);
        }
        else
        {
            if (_writeCallback(_writePos, data, _token))
                return Mp4Status.FileWriteError;
            _writePos += data.Length;
        }

        return Mp4Status.Ok;
    }

    private int WriteFragmentHeader(int trackNum, int dataBytes, int duration, int kind)
    {
        using var bw = new BoxWriter();
        var tr = _tracks[trackNum];

        bw.Begin(Mp4Box.Moof);
        bw.BeginFull(Mp4Box.Mfhd, 0);
        bw.Write32((uint)_fragmentsCount);
        bw.End();

        bw.Begin(Mp4Box.Traf);
        uint flags = (tr.Info.TrackMediaKind == TrackMediaKind.Video) ? 0x20020u : 0x20008u;
        bw.BeginFull(Mp4Box.Tfhd, flags);
        bw.Write32((uint)(trackNum + 1));
        if (tr.Info.TrackMediaKind == TrackMediaKind.Video)
            bw.Write32(0x01010000);
        else
            bw.Write32((uint)duration);
        bw.End();

        bw.BeginFull(Mp4Box.Trun, 0);
        bw.Write32(1);
        var pdataOffset = bw.Reserve(4);

        if (kind == Mp4SampleKind.RandomAccess)
        {
            bw.Write32(0x02000000);
        }
        bw.Write32((uint)duration);
        bw.Write32((uint)dataBytes);
        bw.End();
        bw.End();
        bw.End();

        bw.PatchOffset(pdataOffset, (uint)(bw.Size + 8));

        if (_writeCallback(_writePos, bw.Span, _token))
            return Mp4Status.FileWriteError;
        _writePos += bw.Size;
        return Mp4Status.Ok;
    }

    private int WriteMdatBox(uint size)
    {
        Span<byte> hdr = stackalloc byte[8];
        BigEndian.Write32(hdr, 0, size);
        BigEndian.Write32(hdr, 4, Mp4Box.Mdat);
        if (_writeCallback(_writePos, hdr, _token))
            return Mp4Status.FileWriteError;
        _writePos += 8;
        return Mp4Status.Ok;
    }

    private static int OdSizeOfSize(int size)
    {
        int sos = 1;
        for (int i = size; i > 0x7F; i -= 0x7F)
            sos++;
        return sos;
    }

    private static int ItemsCount(ReadOnlySpan<byte> data)
    {
        int count = 0;
        for (int i = 0; i + 2 <= data.Length;)
        {
            int cb = (data[i] << 8) | data[i + 1];
            count++;
            i += 2 + cb;
        }
        return count;
    }

    private int FlushIndex()
    {
        int ntracks = _tracks.Count;

        for (int ntr = 0; ntr < ntracks; ntr++)
        {
            var tr = _tracks[ntr];
            var err = WritePendingData(tr);
            if (err != Mp4Status.Ok) return err;
        }

        using var bw = new BoxWriter();
        long indexStart = _writePos;

        if (!_sequentialMode)
        {
            long size = _writePos - FtypBox.Length;
            if (size < 0x100000000L)
            {
                bw.Write32(8);
                bw.Write32(Mp4Box.Mdat);
            }
            else
            {
                bw.Write32(1);
                bw.Write32(Mp4Box.Mdat);
                bw.Write32((uint)(size >> 32));
                bw.Write32((uint)size);
            }
            if (_writeCallback(FtypBox.Length, bw.Span, _token))
                return Mp4Status.FileWriteError;
            bw.Reset();
        }

        const uint moovTimescale = 1000;
        bw.Begin(Mp4Box.Moov);
        bw.BeginFull(Mp4Box.Mvhd, 0);
        bw.Write32(0);
        bw.Write32(0);

        if (ntracks > 0)
        {
            var tr = _tracks[0];
            uint duration = GetDuration(tr);
            duration = (uint)(duration * moovTimescale / tr.Info.TimeScale);
            bw.Write32(moovTimescale);
            bw.Write32(duration);
        }

        bw.Write32(0x00010000);
        bw.Write16(0x0100);
        bw.Write16(0);
        bw.Write32(0);
        bw.Write32(0);

        bw.Write32(0x00010000); bw.Write32(0); bw.Write32(0);
        bw.Write32(0); bw.Write32(0x00010000); bw.Write32(0);
        bw.Write32(0); bw.Write32(0); bw.Write32(0x40000000);

        bw.Write32(0); bw.Write32(0); bw.Write32(0);
        bw.Write32(0); bw.Write32(0); bw.Write32(0);

        bw.Write32((uint)ntracks + 1);
        bw.End();

        for (int ntr = 0; ntr < ntracks; ntr++)
        {
            var tr = _tracks[ntr];
            uint duration = GetDuration(tr);
            int samplesCount = tr.Samples.Count;

            if (_enableFragmentation)
                samplesCount = 0;
            else if (samplesCount <= 0)
                continue;

            uint handlerType;
            string? handlerAscii;
            switch (tr.Info.TrackMediaKind)
            {
                case TrackMediaKind.Audio:
                    handlerType = 0x736F756E; // 'soun'
                    handlerAscii = "SoundHandler";
                    break;
                case TrackMediaKind.Video:
                    handlerType = 0x76696465; // 'vide'
                    handlerAscii = "VideoHandler";
                    break;
                default:
                    handlerType = 0x6765736D; // 'gesm'
                    handlerAscii = null;
                    break;
            }

            bw.Begin(Mp4Box.Trak);
            bw.BeginFull(Mp4Box.Tkhd, 7);
            bw.Write32(0);
            bw.Write32(0);
            bw.Write32((uint)(ntr + 1));
            bw.Write32(0);
            bw.Write32((uint)(duration * moovTimescale / tr.Info.TimeScale));
            bw.Write32(0); bw.Write32(0);
            bw.Write16(0);
            bw.Write16(0);
            bw.Write16(tr.Info.TrackMediaKind == TrackMediaKind.Audio ? (ushort)0x0100 : (ushort)0);
            bw.Write16(0);

            bw.Write32(0x00010000); bw.Write32(0); bw.Write32(0);
            bw.Write32(0); bw.Write32(0x00010000); bw.Write32(0);
            bw.Write32(0); bw.Write32(0); bw.Write32(0x40000000);

            if (tr.Info.TrackMediaKind == TrackMediaKind.Video)
            {
                bw.Write32((uint)(tr.Info.U.VideoWidth * 0x10000));
                bw.Write32((uint)(tr.Info.U.VideoHeight * 0x10000));
            }
            else
            {
                bw.Write32(0);
                bw.Write32(0);
            }
            bw.End();

            bw.Begin(Mp4Box.Mdia);
            bw.BeginFull(Mp4Box.Mdhd, 0);
            bw.Write32(0);
            bw.Write32(0);
            bw.Write32(tr.Info.TimeScale);
            bw.Write32(duration);
            {
                int lang = ((tr.Info.Language0 & 31) << 10) | ((tr.Info.Language1 & 31) << 5) | (tr.Info.Language2 & 31);
                bw.Write16((ushort)lang);
            }
            bw.Write16(0);
            bw.End();

            bw.BeginFull(Mp4Box.Hdlr, 0);
            bw.Write32(0);
            bw.Write32(handlerType);
            bw.Write32(0); bw.Write32(0); bw.Write32(0);
            if (handlerAscii != null)
            {
                for (int i = 0; i <= handlerAscii.Length; i++)
                    bw.Write8((byte)(i < handlerAscii.Length ? handlerAscii[i] : 0));
            }
            else
            {
                bw.Write32(0);
            }
            bw.End();

            bw.Begin(Mp4Box.MinF);

            if (tr.Info.TrackMediaKind == TrackMediaKind.Audio)
            {
                bw.BeginFull(Mp4Box.Smhd, 0);
                bw.Write16(0);
                bw.Write16(0);
                bw.End();
            }
            if (tr.Info.TrackMediaKind == TrackMediaKind.Video)
            {
                bw.BeginFull(Mp4Box.Vmhd, 1);
                bw.Write16(0);
                bw.Write16(0); bw.Write16(0); bw.Write16(0);
                bw.End();
            }

            bw.Begin(Mp4Box.DinF);
            bw.BeginFull(Mp4Box.DreF, 0);
            bw.Write32(1);
            bw.BeginFull(Mp4Box.Url, 1);
            bw.End();
            bw.End();
            bw.End();

            bw.Begin(Mp4Box.Stbl);
            bw.BeginFull(Mp4Box.Stsd, 0);
            bw.Write32(1);

            bool isVideoAvcHevc = tr.Info.TrackMediaKind == TrackMediaKind.Video &&
                (tr.Info.ObjectTypeIndication == Mp4ObjectType.Avc ||
                 tr.Info.ObjectTypeIndication == Mp4ObjectType.Hevc);

            if (tr.Info.TrackMediaKind == TrackMediaKind.Audio || tr.Info.TrackMediaKind == TrackMediaKind.Private)
            {
                uint sampleEntryType = tr.Info.TrackMediaKind == TrackMediaKind.Audio ? Mp4Box.Mp4a : Mp4Box.Mp4s;
                bw.Begin(sampleEntryType);
                bw.Write32(0); bw.Write16(0);
                bw.Write16(1);

                if (tr.Info.TrackMediaKind == TrackMediaKind.Audio)
                {
                    bw.Write32(0); bw.Write32(0);
                    bw.Write16(tr.Info.U.AudioChannelCount);
                    bw.Write16(16);
                    bw.Write32(0);
                    bw.Write32(tr.Info.TimeScale << 16);
                }

                bw.BeginFull(Mp4Box.Esds, 0);
                if (tr.Vsps.Count > 0)
                {
                    int dsiBytes = tr.Vsps.Count - 2;
                    int dsiSizeSize = OdSizeOfSize(dsiBytes);
                    int dcdBytes = dsiBytes + dsiSizeSize + 1 + (1 + 1 + 3 + 4 + 4);
                    int dcdSizeSize = OdSizeOfSize(dcdBytes);
                    int esdBytes = dcdBytes + dcdSizeSize + 1 + 3;

                    bw.Write8(3);
                    WriteOdLen(bw, esdBytes);
                    bw.Write16(0);
                    bw.Write8(0);

                    bw.Write8(4);
                    WriteOdLen(bw, dcdBytes);
                    bw.Write8((byte)(tr.Info.TrackMediaKind == TrackMediaKind.Audio ? 0x40 : 208));
                    bw.Write8((byte)(tr.Info.TrackMediaKind == TrackMediaKind.Audio ? (5 << 2) : (32 << 2)));
                    bw.Write24((uint)(tr.Info.U.AudioChannelCount * 6144 / 8));
                    bw.Write32(0);
                    bw.Write32(0);

                    bw.Write8(5);
                    WriteOdLen(bw, dsiBytes);
                    for (int i = 0; i < dsiBytes; i++)
                        bw.Write8(tr.Vsps[2 + i]);
                }
                bw.End();
                bw.End();
            }

            if (isVideoAvcHevc)
            {
                uint sampleEntryType = tr.Info.ObjectTypeIndication == Mp4ObjectType.Avc ? Mp4Box.Avc1 : Mp4Box.Hvc1;
                bw.Begin(sampleEntryType);
                bw.Write16(0); bw.Write16(0); bw.Write16(0);
                bw.Write16(1);

                bw.Write16(0); bw.Write16(0);
                bw.Write32(0); bw.Write32(0); bw.Write32(0);
                bw.Write16((ushort)tr.Info.U.VideoWidth);
                bw.Write16((ushort)tr.Info.U.VideoHeight);
                bw.Write32(0x00480000);
                bw.Write32(0x00480000);
                bw.Write32(0);
                bw.Write16(1);
                for (int i = 0; i < 32; i++) bw.Write8(0);
                bw.Write16(24);
                bw.Write16(0xFFFF);

                if (tr.Info.ObjectTypeIndication == Mp4ObjectType.Avc)
                {
                    bw.Begin(Mp4Box.AvcC);
                    bw.Write8(1);
                    bw.Write8(tr.Vsps[2 + 1]);
                    bw.Write8(tr.Vsps[2 + 2]);
                    bw.Write8(tr.Vsps[2 + 3]);
                    bw.Write8(255);
                    bw.Write8((byte)(0xE0 | ItemsCount(CollectionsMarshal.AsSpan(tr.Vsps))));
                    bw.WriteRaw(CollectionsMarshal.AsSpan(tr.Vsps));
                    bw.Write8((byte)ItemsCount(CollectionsMarshal.AsSpan(tr.Vpps)));
                    bw.WriteRaw(CollectionsMarshal.AsSpan(tr.Vpps));
                    bw.End();
                }
                else
                {
                    bw.Begin(Mp4Box.HvcC);
                    bw.Write8(1);
                    bw.Write8(1);
                    bw.Write32(0x60000000);
                    bw.Write16(0);
                    bw.Write32(0);
                    bw.Write8(0);
                    bw.Write16(0xF000);
                    bw.Write8(0xFC);
                    bw.Write8(0xFC);
                    bw.Write8(0xF8);
                    bw.Write8(0xF8);
                    bw.Write16(0);
                    bw.Write8(3);
                    bw.Write8(3);

                    int numVPS = ItemsCount(CollectionsMarshal.AsSpan(tr.Vvps));
                    bw.Write8((byte)((1 << 7) | (32 & 0x3F)));
                    bw.Write16((ushort)numVPS);
                    bw.WriteRaw(CollectionsMarshal.AsSpan(tr.Vvps));

                    bw.Write8((byte)((1 << 7) | (33 & 0x3F)));
                    bw.Write16((ushort)ItemsCount(CollectionsMarshal.AsSpan(tr.Vsps)));
                    bw.WriteRaw(CollectionsMarshal.AsSpan(tr.Vsps));

                    bw.Write8((byte)((1 << 7) | (34 & 0x3F)));
                    bw.Write16((ushort)ItemsCount(CollectionsMarshal.AsSpan(tr.Vpps)));
                    bw.WriteRaw(CollectionsMarshal.AsSpan(tr.Vpps));
                    bw.End();
                }
                bw.End();
            }

            // MJPEG video track
            if (tr.Info.TrackMediaKind == TrackMediaKind.Video &&
                tr.Info.ObjectTypeIndication == Mp4ObjectType.Mjpeg)
            {
                // 'jpeg' sample entry for MJPEG
                bw.Begin(0x6A706567); // 'jpeg'
                bw.Write16(0); bw.Write16(0); bw.Write16(0);
                bw.Write16(1);
                bw.Write16(0); bw.Write16(0);
                bw.Write32(0); bw.Write32(0); bw.Write32(0);
                bw.Write16((ushort)tr.Info.U.VideoWidth);
                bw.Write16((ushort)tr.Info.U.VideoHeight);
                bw.Write32(0x00480000);
                bw.Write32(0x00480000);
                bw.Write32(0);
                bw.Write16(1);
                for (int i = 0; i < 32; i++) bw.Write8(0);
                bw.Write16(24);
                bw.Write16(0xFFFF);
                bw.End();
            }

            bw.End();

            // stts - Time To Sample Box
            bw.BeginFull(Mp4Box.Stts, 0);
            {
                int entryCount = 0;
                int cnt = 1;
                var pos = bw.Reserve(4);
                for (int i = 0; i < samplesCount; i++, cnt++)
                {
                    if (i == samplesCount - 1 || tr.Samples[i].Duration != tr.Samples[i + 1].Duration)
                    {
                        bw.Write32((uint)cnt);
                        bw.Write32(tr.Samples[i].Duration);
                        cnt = 0;
                        entryCount++;
                    }
                }
                bw.PatchOffset(pos, (uint)entryCount);
            }
            bw.End();

            // stsc - Sample To Chunk Box
            bw.BeginFull(Mp4Box.Stsc, 0);
            if (_enableFragmentation)
            {
                bw.Write32(0);
            }
            else
            {
                bw.Write32(1);
                bw.Write32(1);
                bw.Write32(1);
                bw.Write32(1);
            }
            bw.End();

            // stsz - Sample Size Box
            bw.BeginFull(Mp4Box.Stsz, 0);
            bw.Write32(0);
            bw.Write32((uint)samplesCount);
            for (int i = 0; i < samplesCount; i++)
                bw.Write32((uint)tr.Samples[i].Size);
            bw.End();

            // Chunk Offset Box
            bool is64Bit = samplesCount > 0 && tr.Samples[^1].Offset > 0xFFFFFFFF;
            if (!is64Bit)
            {
                bw.BeginFull(Mp4Box.Stco, 0);
                bw.Write32((uint)samplesCount);
                for (int i = 0; i < samplesCount; i++)
                    bw.Write32((uint)tr.Samples[i].Offset);
            }
            else
            {
                bw.BeginFull(Mp4Box.Co64, 0);
                bw.Write32((uint)samplesCount);
                for (int i = 0; i < samplesCount; i++)
                {
                    bw.Write32((uint)(tr.Samples[i].Offset >> 32));
                    bw.Write32((uint)tr.Samples[i].Offset);
                }
            }
            bw.End();

            // stss - Sync Sample Box
            {
                int raCount = 0;
                for (int i = 0; i < samplesCount; i++)
                    if (tr.Samples[i].FlagRandomAccess != 0) raCount++;
                if (raCount != samplesCount)
                {
                    bw.BeginFull(Mp4Box.Stss, 0);
                    bw.Write32((uint)raCount);
                    for (int i = 0; i < samplesCount; i++)
                        if (tr.Samples[i].FlagRandomAccess != 0)
                            bw.Write32((uint)(i + 1));
                    bw.End();
                }
            }

            bw.End(); // stbl
            bw.End(); // minf
            bw.End(); // mdia
            bw.End(); // trak
        }

        if (_textComment != null)
        {
            bw.Begin(Mp4Box.Udta);
            bw.BeginFull(Mp4Box.Meta, 0);
            bw.BeginFull(Mp4Box.Hdlr, 0);
            bw.Write32(0);
            bw.Write32(0x6D646972); // 'mdir'
            bw.Write32(0); bw.Write32(0); bw.Write32(0);
            bw.Write32(0);
            bw.End();
            bw.Begin(Mp4Box.Ilst);
            bw.Begin(Mp4Box.Ccmt);
            bw.Begin(Mp4Box.Data);
            bw.Write32(1);
            bw.Write32(0);
            for (int i = 0; i <= _textComment.Length; i++)
                bw.Write8((byte)(i < _textComment.Length ? _textComment[i] : 0));
            bw.End();
            bw.End();
            bw.End();
            bw.End();
            bw.End();
            bw.End();
        }

        if (_enableFragmentation)
        {
            bw.Begin(Mp4Box.Mvex);
            bw.BeginFull(Mp4Box.MehD, 0);
            var tr0 = _tracks[0];
            bw.Write32(GetDuration(tr0));
            bw.End();
            for (int ntr = 0; ntr < ntracks; ntr++)
            {
                bw.BeginFull(Mp4Box.Trex, 0);
                bw.Write32((uint)(ntr + 1));
                bw.Write32(1);
                bw.Write32(0);
                bw.Write32(0);
                bw.Write32(0);
                bw.End();
            }
            bw.End();
        }

        bw.End(); // moov

        if (_writeCallback(_writePos, bw.Span, _token))
            return Mp4Status.FileWriteError;
        _writePos += bw.Size;

        return Mp4Status.Ok;
    }

    public int Close()
    {
        int err = Mp4Status.Ok;
        if (!_enableFragmentation)
            err = FlushIndex();

        foreach (var tr in _tracks)
        {
            tr.Samples.Clear();
            tr.PendingSample.Clear();
            tr.Vsps.Clear();
            tr.Vpps.Clear();
            tr.Vvps.Clear();
        }
        _tracks.Clear();
        return err;
    }

    public void Dispose()
    {
        Close();
    }

    private static void WriteOdLen(BoxWriter bw, int size)
    {
        if (size > 0x7F)
        {
            do
            {
                size -= 0x7F;
                bw.Write8(0xFF);
            } while (size > 0x7F);
        }
        bw.Write8((byte)size);
    }
}

internal sealed class BoxWriter : IDisposable
{
    private byte[] _buffer = new byte[65536];
    private int _position;
    private readonly Stack<(int pos, uint boxType)> _stack = new();

    public ReadOnlySpan<byte> Span => new(_buffer, 0, _position);
    public int Size => _position;

    public void Reset()
    {
        _position = 0;
        _stack.Clear();
    }

    public void Begin(uint boxType)
    {
        _stack.Push((_position, boxType));
        _position += 4;
        Write32(boxType);
    }

    public void BeginFull(uint boxType, uint flags)
    {
        _stack.Push((_position, boxType));
        _position += 4;
        Write32(boxType);
        Write32(flags);
    }

    public void End()
    {
        var (startPos, _) = _stack.Pop();
        int size = _position - startPos;
        BigEndian.Write32(_buffer, startPos, (uint)size);
    }

    public int Reserve(int bytes)
    {
        int pos = _position;
        EnsureCapacity(bytes);
        _position += bytes;
        return pos;
    }

    public void PatchOffset(int position, uint value)
    {
        BigEndian.Write32(_buffer, position, value);
    }

    public void Write8(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    public void Write16(ushort value)
    {
        EnsureCapacity(2);
        BigEndian.Write16(_buffer, _position, value);
        _position += 2;
    }

    public void Write24(uint value)
    {
        EnsureCapacity(3);
        _buffer[_position++] = (byte)(value >> 16);
        _buffer[_position++] = (byte)(value >> 8);
        _buffer[_position++] = (byte)value;
    }

    public void Write32(uint value)
    {
        EnsureCapacity(4);
        BigEndian.Write32(_buffer, _position, value);
        _position += 4;
    }

    public void WriteRaw(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(new Span<byte>(_buffer, _position, data.Length));
        _position += data.Length;
    }

    private void EnsureCapacity(int needed)
    {
        if (_position + needed > _buffer.Length)
        {
            int newSize = Math.Max(_buffer.Length * 2, _position + needed + 4096);
            Array.Resize(ref _buffer, newSize);
        }
    }

    public void Dispose() { }
}

internal static class BigEndian
{
    public static void Write16(byte[] buf, int off, ushort v)
    {
        buf[off] = (byte)(v >> 8);
        buf[off + 1] = (byte)v;
    }

    public static void Write32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }

    public static void Write32(Span<byte> buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }
}

public sealed class H26xWriter : IDisposable
{
    private Mp4Muxer _mux = null!;
    public int TrackId { get; private set; }
    public bool IsHevc { get; private set; }
    public bool NeedVps { get; private set; }
    public bool NeedSps { get; private set; }
    public bool NeedPps { get; private set; }
    public bool NeedIdr { get; private set; }

    public int Init(Mp4Muxer mux, int width, int height, bool isHevc)
    {
        var tr = new Mp4TrackInfo
        {
            Language0 = (byte)'u',
            Language1 = (byte)'n',
            Language2 = (byte)'d',
            ObjectTypeIndication = isHevc ? Mp4ObjectType.Hevc : Mp4ObjectType.Avc,
            TrackMediaKind = TrackMediaKind.Video,
            TimeScale = 90000,
            DefaultDuration = 0
        };
        tr.U.VideoWidth = width;
        tr.U.VideoHeight = height;

        TrackId = mux.AddTrack(tr);
        _mux = mux;
        IsHevc = isHevc;
        NeedVps = isHevc;
        NeedSps = true;
        NeedPps = true;
        NeedIdr = true;
        return Mp4Status.Ok;
    }

    public int WriteNal(ReadOnlySpan<byte> nalData, uint timeStamp90kHz)
    {
        const int HevcNalVps = 32;
        const int HevcNalSps = 33;
        const int HevcNalPps = 34;
        const int HevcNalBlaWLp = 16;
        const int HevcNalCraNut = 21;

        int offset = 0;
        int err = Mp4Status.Ok;

        while (offset < nalData.Length)
        {
            if (!FindNalUnit(nalData, ref offset, out int nalLen, out ReadOnlySpan<byte> nal))
                break;

            if (IsHevc)
            {
                int payloadType = (nal[0] >> 1) & 0x3F;
                bool isIntra = payloadType >= HevcNalBlaWLp && payloadType <= HevcNalCraNut;

                if (isIntra && !NeedSps && !NeedPps && !NeedVps)
                    NeedIdr = false;

                switch (payloadType)
                {
                    case HevcNalVps:
                        _mux.SetVps(TrackId, nal);
                        NeedVps = false;
                        break;
                    case HevcNalSps:
                        _mux.SetSps(TrackId, nal);
                        NeedSps = false;
                        break;
                    case HevcNalPps:
                        _mux.SetPps(TrackId, nal);
                        NeedPps = false;
                        break;
                    default:
                        if (NeedVps || NeedSps || NeedPps || NeedIdr)
                            return Mp4Status.BadArguments;
                        var tmp = new byte[4 + nalLen];
                        BigEndian.Write32(tmp, 0, (uint)nalLen);
                        nal.CopyTo(new Span<byte>(tmp, 4, nalLen));
                        int kind = isIntra ? Mp4SampleKind.RandomAccess : Mp4SampleKind.Default;
                        err = _mux.PutSample(TrackId, tmp, (int)timeStamp90kHz, kind);
                        break;
                }
            }
            else
            {
                int payloadType = nal[0] & 0x1F;
                if (payloadType == 9) continue;

                switch (payloadType)
                {
                    case 7:
                        _mux.SetSps(TrackId, nal);
                        NeedSps = false;
                        break;
                    case 8:
                        _mux.SetPps(TrackId, nal);
                        NeedPps = false;
                        break;
                    case 5:
                        if (NeedSps) return Mp4Status.BadArguments;
                        NeedIdr = false;
                        goto default;
                    default:
                        if (NeedSps) return Mp4Status.BadArguments;
                        if (!NeedPps && !NeedIdr)
                        {
                            var tmp = new byte[4 + nalLen];
                            BigEndian.Write32(tmp, 0, (uint)nalLen);
                            nal.CopyTo(new Span<byte>(tmp, 4, nalLen));
                            int kind = payloadType == 5 ? Mp4SampleKind.RandomAccess : Mp4SampleKind.Default;
                            err = _mux.PutSample(TrackId, tmp, (int)timeStamp90kHz, kind);
                        }
                        break;
                }
            }

            if (err != Mp4Status.Ok) break;
        }

        return err;
    }

    public void Dispose()
    {
    }

    private static bool FindNalUnit(ReadOnlySpan<byte> data, ref int offset, out int nalLen, out ReadOnlySpan<byte> nal)
    {
        nalLen = 0;
        nal = default;

        if (!FindStartCode(data.Slice(offset), out int startCodeLen, out int startIdx))
            return false;

        int nalStart = offset + startIdx;
        int remainingStart = nalStart + startCodeLen;
        var remaining = data.Slice(remainingStart);

        if (FindStartCode(remaining, out int nextStartCodeLen, out int nextIdx))
        {
            int stop = nextIdx + remainingStart;
            while (stop > nalStart + startCodeLen && data[stop - 1] == 0) stop--;
            nalLen = stop - nalStart - startCodeLen;
            offset = stop;
        }
        else
        {
            nalLen = data.Length - nalStart - startCodeLen;
            offset = data.Length;
        }

        nal = data.Slice(nalStart + startCodeLen, nalLen);
        return true;
    }

    private static bool FindStartCode(ReadOnlySpan<byte> data, out int zcount, out int startIdx)
    {
        zcount = 0;
        startIdx = 0;
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] == 0)
            {
                int zeroCnt = 1;
                while (i + zeroCnt < data.Length && data[i + zeroCnt] == 0) zeroCnt++;
                if (zeroCnt >= 2 && i + zeroCnt < data.Length && data[i + zeroCnt] == 1)
                {
                    zcount = zeroCnt + 1;
                    startIdx = i;
                    return true;
                }
                i += zeroCnt;
            }
            else
            {
                i++;
            }
        }
        return false;
    }
}
