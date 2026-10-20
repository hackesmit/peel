// Review round 3 fix of the image editor windows (hq-plct.10): a PNG whose
// XMP tiff:Orientation sits in a compressed zTXt or iTXt chunk. Pillow
// inflates it and opens the picture turned, so OrientationProbe must inflate
// it too (or doubt) and send the preview through the engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Peel.Editors;
using Xunit;

namespace Peel.Tests;

public class Round3ImageEditorsTests
{
    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] Png(string type, byte[] data)
    {
        var file = new List<byte>(Signature);
        file.AddRange(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
        file.AddRange(Encoding.ASCII.GetBytes(type));
        file.AddRange(data);
        file.AddRange(new byte[4]);
        file.AddRange(new byte[] { 0, 0, 0, 0 });
        file.AddRange(Encoding.ASCII.GetBytes("IEND"));
        file.AddRange(new byte[4]);
        return file.ToArray();
    }

    private static byte[] Deflate(string text)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            zlib.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static string Xmp(int orientation) =>
        $"<x:xmpmeta><rdf:Description tiff:Orientation=\"{orientation}\"/></x:xmpmeta>";

    private static byte[] ZTxt(byte[] compressed) =>
        Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0").Concat(compressed).ToArray();

    // keyword NUL, flag 1, method 0, empty language NUL, empty translation NUL
    private static byte[] ITxt(byte[] compressed) =>
        Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\u0001\0\0\0").Concat(compressed).ToArray();

    [Fact]
    public void ACompressedXmpOrientationIsFound()
    {
        Assert.True(OrientationProbe.MayBeTurned(Png("zTXt", ZTxt(Deflate(Xmp(6))))));
        Assert.True(OrientationProbe.MayBeTurned(Png("iTXt", ITxt(Deflate(Xmp(8))))));
        Assert.True(OrientationProbe.MayBeTurned(Png("iTXt",
            ITxt(Deflate("<x:xmpmeta><tiff:Orientation>3</tiff:Orientation></x:xmpmeta>")))));
    }

    [Fact]
    public void ACompressedChunkThatCannotBeReadIsDoubt()
    {
        Assert.True(OrientationProbe.MayBeTurned(Png("zTXt", ZTxt(new byte[] { 0x78, 0x9C, 0xFF, 0xFF }))));
        Assert.True(OrientationProbe.MayBeTurned(Png("iTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\u0001\0no-nul"))));
        // Past what pillow inflates: doubt, not upright.
        Assert.True(OrientationProbe.MayBeTurned(Png("zTXt", ZTxt(Deflate(new string(' ', 2 * 1024 * 1024))))));
    }

    [Fact]
    public void ACompressedUprightOrUnrelatedChunkStaysOnTheFastRoad()
    {
        Assert.False(OrientationProbe.MayBeTurned(Png("zTXt", ZTxt(Deflate(Xmp(1))))));
        Assert.False(OrientationProbe.MayBeTurned(Png("iTXt", ITxt(Deflate(Xmp(1))))));
        Assert.False(OrientationProbe.MayBeTurned(Png("zTXt",
            Encoding.ASCII.GetBytes("Comment\0\0").Concat(Deflate("a comment")).ToArray())));
        // An uncompressed iTXt is the plain search's job, as before.
        Assert.False(OrientationProbe.MayBeTurned(Png("iTXt",
            Encoding.ASCII.GetBytes("Comment\0\0\0\0\0a comment"))));
    }
}
