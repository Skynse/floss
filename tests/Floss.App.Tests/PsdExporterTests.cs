namespace Floss.App.Tests;

public class PsdExporterTests
{
    [Fact]
    public void Export_CanBeReadBack()
    {
        var bytes = ExportSamplePsd();
        using var stream = new MemoryStream(bytes);

        var psd = PsdReader.Read(stream);

        TestAssertions.Equal(2, psd.Width);
        TestAssertions.Equal(2, psd.Height);
        TestAssertions.Equal(1, psd.Layers.Count);

        var layer = psd.Layers[0] as PsdLayerNode
            ?? throw new InvalidOperationException("Expected exported PSD to contain a paint layer.");
        TestAssertions.Equal("Ink", layer.Name);
        TestAssertions.Equal("mul ", layer.BlendMode);
        TestAssertions.Equal((byte)127, layer.Opacity);
        TestAssertions.False(layer.IsVisible);
        TestAssertions.Equal(0, layer.Left);
        TestAssertions.Equal(0, layer.Top);
        TestAssertions.Equal(2, layer.Width);
        TestAssertions.Equal(2, layer.Height);
        TestAssertions.True(layer.Bgra != null, "Expected decoded layer pixels.");
        TestAssertions.SequenceEqual(new byte[] { 10, 20, 30, 40 }, layer.Bgra!.Take(4));
        TestAssertions.SequenceEqual(new byte[] { 50, 60, 70, 80 }, layer.Bgra!.Skip(4).Take(4));
    }

    [Fact]
    public void Export_WritesValidLayerInfoStructure()
    {
        var data = ExportSamplePsd();
        var r = new PsdBytes(data);

        TestAssertions.Equal("8BPS", r.Ascii(4));
        TestAssertions.Equal((ushort)1, r.U16());
        r.Skip(6);
        TestAssertions.Equal((ushort)4, r.U16());
        TestAssertions.Equal(2u, r.U32());
        TestAssertions.Equal(2u, r.U32());
        TestAssertions.Equal((ushort)8, r.U16());
        TestAssertions.Equal((ushort)3, r.U16());

        r.Skip((int)r.U32()); // color mode data
        r.Skip((int)r.U32()); // image resources

        var layerMaskLen = r.U32();
        var layerMaskEnd = checked(r.Position + (int)layerMaskLen);
        TestAssertions.True(layerMaskLen > 0);

        var layerInfoLen = r.U32();
        var layerInfoEnd = checked(r.Position + (int)layerInfoLen);
        TestAssertions.True(layerInfoLen > 0);
        TestAssertions.Equal((short)-1, r.I16());

        TestAssertions.Equal(0, r.I32()); // top
        TestAssertions.Equal(0, r.I32()); // left
        TestAssertions.Equal(2, r.I32()); // bottom
        TestAssertions.Equal(2, r.I32()); // right
        TestAssertions.Equal((ushort)4, r.U16());

        var channelLengths = new List<uint>();
        TestAssertions.Equal((short)-1, r.I16());
        channelLengths.Add(r.U32());
        TestAssertions.Equal((short)0, r.I16());
        channelLengths.Add(r.U32());
        TestAssertions.Equal((short)1, r.I16());
        channelLengths.Add(r.U32());
        TestAssertions.Equal((short)2, r.I16());
        channelLengths.Add(r.U32());
        TestAssertions.True(channelLengths.All(length => length > 6));

        TestAssertions.Equal("8BIM", r.Ascii(4));
        TestAssertions.Equal("mul ", r.Ascii(4));
        TestAssertions.Equal((byte)127, r.Byte());
        TestAssertions.Equal((byte)0, r.Byte());
        TestAssertions.Equal((byte)2, r.Byte());
        TestAssertions.Equal((byte)0, r.Byte());

        var extraLen = r.U32();
        var extraEnd = checked(r.Position + (int)extraLen);
        TestAssertions.True(extraLen >= 12);
        TestAssertions.Equal(0u, r.U32());
        TestAssertions.Equal(0u, r.U32());
        var nameLen = r.Byte();
        TestAssertions.Equal((byte)3, nameLen);
        TestAssertions.Equal("Ink", r.Ascii(nameLen));
        r.Position = extraEnd;

        foreach (var length in channelLengths)
        {
            var channelEnd = checked(r.Position + (int)length);
            TestAssertions.Equal((ushort)1, r.U16());
            var row0 = r.U16();
            var row1 = r.U16();
            TestAssertions.Equal(length - 6, (uint)(row0 + row1));
            r.Position = channelEnd;
        }

        TestAssertions.Equal(layerInfoEnd, r.Position);
        TestAssertions.Equal(0u, r.U32());
        TestAssertions.Equal(layerMaskEnd, r.Position);

        TestAssertions.Equal((ushort)0, r.U16());
        TestAssertions.Equal(2 * 2 * 4, data.Length - r.Position);
    }

    [Fact]
    public void Export_PreservesFolderHierarchy()
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
        TestAssertions.Equal(2, psd.Layers.Count);
        TestAssertions.Equal("Background", psd.Layers[0].Name);

        var group = psd.Layers[1] as PsdGroupNode
            ?? throw new InvalidOperationException("Expected exported PSD to preserve the folder.");
        TestAssertions.Equal("Folder A", group.Name);
        TestAssertions.Equal("pass", group.BlendMode);
        TestAssertions.False(group.IsOpen);
        TestAssertions.Equal(1, group.Children.Count);

        var child = group.Children[0] as PsdLayerNode
            ?? throw new InvalidOperationException("Expected folder child to remain inside the group.");
        TestAssertions.Equal("Ink", child.Name);
        TestAssertions.True(child.Bgra != null);
        TestAssertions.SequenceEqual(new byte[] { 4, 5, 6, 255 }, child.Bgra!.Skip((1 * child.Width + 1) * 4).Take(4));

        stream.Position = 0;
        var imported = PsdImporter.Load(stream);
        var importedGroup = imported.Layers.Single(layer => layer.IsGroup);
        TestAssertions.Equal("Folder A", importedGroup.Name);
        TestAssertions.Equal("PassThrough", importedGroup.BlendMode);
        TestAssertions.False(importedGroup.IsOpen);
        TestAssertions.Equal(1, importedGroup.Children.Count);
        TestAssertions.Equal("Ink", importedGroup.Children[0].Name);
        TestAssertions.True(ReferenceEquals(importedGroup, importedGroup.Children[0].Parent));
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

