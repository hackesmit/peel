// Round 1 fixes of the image editor windows (hq-plct.10), one group per
// finding. Redaction is a data safety surface, so the rule every redact test
// here pins is: the area destroyed is always at least the area the user saw
// covered.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Peel.Editors;
using Peel.Editors.Image;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

public class Round1ImageEditorsTests
{
    private const double StageWidth = 900;
    private const double StageHeight = 560;

    // --- 1. rectangle drags round outward and reach the edge -----------------

    [Fact]
    public void ADragEdgeIsAContinuousCoordinateUpToTheFullSize()
    {
        var map = new PreviewMap(400, 300, StageWidth, StageHeight);
        var corner = map.ToImage(new Point(map.ViewSize.Width, map.ViewSize.Height));
        Assert.Equal(400, corner.X, 6);
        Assert.Equal(300, corner.Y, 6);
        var past = map.ToImage(new Point(1e6, 1e6));
        Assert.Equal(new Point(400, 300), past);
        var nan = map.ToImage(new Point(double.NaN, double.PositiveInfinity));
        Assert.True(double.IsFinite(nan.X) && double.IsFinite(nan.Y));
    }

    [Fact]
    public void AnEdgeToEdgeRedactDragDestroysEveryPixel()
    {
        var map = new PreviewMap(400, 300, StageWidth, StageHeight);
        var region = PixelBox.Covering(
            map, map.ToImage(new Point(-10, -10)), map.ToImage(new Point(5000, 5000)));
        Assert.Equal(new Int32Rect(0, 0, 400, 300), region);
        var plan = new RedactPlan(ImageEditorTests.Schema("image.redact"));
        plan.Add(region);
        using var json = JsonDocument.Parse(plan.ToJson());
        var sent = json.RootElement[0];
        Assert.Equal(0, sent.GetProperty("x").GetInt32());
        Assert.Equal(0, sent.GetProperty("y").GetInt32());
        Assert.Equal(400, sent.GetProperty("w").GetInt32());
        Assert.Equal(300, sent.GetProperty("h").GetInt32());
    }

    [Fact]
    public void TheNearEdgeFloorsAndTheFarEdgeCeils()
    {
        var box = PixelBox.FromDrag(new Point(102.5, 7.9), new Point(10.2, 50.01), 400, 300);
        Assert.Equal(10, box.X);
        Assert.Equal(7, box.Y);
        Assert.Equal(103, box.X + box.Width);
        Assert.Equal(51, box.Y + box.Height);
    }

    // Every pixel any part of the drag touched is inside the region, for
    // random drags on pictures of every shape and zoom.
    [Fact]
    public void ARandomDragNeverLeavesATouchedPixelOutsideTheRegion()
    {
        var random = new Random(6029);
        foreach (var (w, h) in new[] { (4000, 3000), (8000, 6000), (400, 300), (20, 10), (3, 900), (1200, 7) })
        {
            var map = new PreviewMap(w, h, StageWidth, StageHeight);
            for (var i = 0; i < 500; i++)
            {
                var a = new Point(random.NextDouble() * (map.ViewSize.Width + 40) - 20, random.NextDouble() * (map.ViewSize.Height + 40) - 20);
                var b = new Point(random.NextDouble() * (map.ViewSize.Width + 40) - 20, random.NextDouble() * (map.ViewSize.Height + 40) - 20);
                var from = map.ToImage(a);
                var to = map.ToImage(b);
                var region = PixelBox.Covering(map, from, to);
                Assert.True(region.X >= 0 && region.Y >= 0 && region.X + region.Width <= w && region.Y + region.Height <= h);
                Assert.True(region.X <= Math.Floor(Math.Min(from.X, to.X)));
                Assert.True(region.Y <= Math.Floor(Math.Min(from.Y, to.Y)));
                Assert.True(region.X + region.Width >= Math.Min(w, Math.Ceiling(Math.Max(from.X, to.X))));
                Assert.True(region.Y + region.Height >= Math.Min(h, Math.Ceiling(Math.Max(from.Y, to.Y))));
            }
        }
    }

    // --- 2. the cover drawn is the region sent ---------------------------------

    // lucy's case: a 4000 by 3000 photo, a drag from (0,0) to (0.5,0.5) on the
    // overlay. The region it means is a few pixels; drawn at its own size it
    // is under one unit wide. The fix grows the REGION until it is visible,
    // and the window draws exactly that region, so the cover is never wider
    // than what the engine destroys.
    [Fact]
    public void ATinyRegionIsGrownSoTheCoverAndTheSubmissionAreTheSameRectangle()
    {
        var map = new PreviewMap(4000, 3000, StageWidth, StageHeight);
        var from = map.ToImage(new Point(0, 0));
        var to = map.ToImage(new Point(0.5, 0.5));
        var dragged = PixelBox.FromDrag(from, to, map.ImageWidth, map.ImageHeight);
        var region = PixelBox.Covering(map, from, to);
        var cover = map.ToView(region.X, region.Y, region.Width, region.Height);
        Assert.True(cover.Width >= 1 - 1e-9, $"cover {cover.Width} wide");
        Assert.True(cover.Height >= 1 - 1e-9, $"cover {cover.Height} high");
        Assert.True(region.Width >= dragged.Width && region.Height >= dragged.Height);
        Assert.True(region.X <= dragged.X && region.Y <= dragged.Y);
        // The cover is exactly the submitted region on the preview, to the unit.
        Assert.Equal(region.X * map.Scale, cover.X, 9);
        Assert.Equal(region.Width * map.Scale, cover.Width, 9);
    }

    [Fact]
    public void GrowingAtTheEdgeStaysInsideAndStillCoversTheOriginal()
    {
        var map = new PreviewMap(8000, 6000, StageWidth, StageHeight);
        var original = new Int32Rect(7998, 5999, 2, 1);
        var grown = PixelBox.AtLeastVisible(original, map.Scale, 8000, 6000);
        Assert.True(grown.X + grown.Width == 8000 && grown.Y + grown.Height == 6000);
        Assert.True(grown.X <= original.X && grown.Y <= original.Y);
        Assert.True(grown.Width * map.Scale >= 1 - 1e-9 && grown.Height * map.Scale >= 1 - 1e-9);
        // A region that is already visible is left exactly as it is.
        var big = new Int32Rect(100, 100, 400, 300);
        Assert.Equal(big, PixelBox.AtLeastVisible(big, map.Scale, 8000, 6000));
        // On an enlarged small picture nothing grows: one pixel is 8 units.
        var zoomed = new PreviewMap(20, 10, StageWidth, StageHeight);
        Assert.Equal(new Int32Rect(3, 3, 1, 1), PixelBox.AtLeastVisible(new Int32Rect(3, 3, 1, 1), zoomed.Scale, 20, 10));
    }

    // --- 3. preview and apply are bound to the same bytes ----------------------

    [Fact]
    public async Task AFileReplacedAfterItsPreviewIsRefused()
    {
        var folder = Folder();
        try
        {
            var file = Path.Combine(folder, "face.png");
            File.WriteAllBytes(file, Png(40, 30, Colors.Red));
            var (preview, problem) = await PreviewSource.LoadAsync(file, new EngineClient(), CancellationToken.None);
            Assert.True(preview is not null, problem);
            Assert.Equal(PreviewSource.HashOf(file), preview!.SourceHash);
            Assert.True(preview.StillMatches(file, out _));

            // Same size, different picture, same name: the redaction drawn on
            // the first one must not be applied to it.
            File.WriteAllBytes(file, Png(40, 30, Colors.Blue));
            Assert.False(preview.StillMatches(file, out var why));
            Assert.Contains("Open it again", why, StringComparison.Ordinal);

            File.Delete(file);
            Assert.False(preview.StillMatches(file, out _));
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // --- 4. one quantized factor for preview, readout and parameters -----------

    [Fact]
    public void AFactorJustOffNeutralIsNeutralEverywhere()
    {
        var plan = new EditPlan(ImageEditorTests.Schema("image.edit"));
        plan.Brightness = 0.996;
        Assert.Equal(1.0, plan.Brightness);
        Assert.Equal("1.00", EditPlan.Readout(plan.Brightness));
        Assert.False(plan.ToParams().ContainsKey("brightness"));
    }

    [Fact]
    public void TheValueSentIsTheValuePaintedAndShown()
    {
        var plan = new EditPlan(ImageEditorTests.Schema("image.edit"));
        plan.Contrast = 1.23456;
        plan.Saturation = 9.0;
        plan.Brightness = double.NaN;
        Assert.Equal(1.23, plan.Contrast);
        Assert.Equal("1.23", EditPlan.Readout(plan.Contrast));
        Assert.Equal(4.0, plan.Saturation);
        Assert.Equal(1.0, plan.Brightness);
        var sent = plan.ToParams();
        Assert.Equal("1.23", sent["contrast"]);
        Assert.Equal("4", sent["saturation"]);
        Assert.False(sent.ContainsKey("brightness"));
        // The number sent parses back to exactly the factor the preview used.
        Assert.Equal(plan.Contrast, double.Parse(sent["contrast"], System.Globalization.CultureInfo.InvariantCulture));
    }

    // --- 5. orientation in every container pillow reads ------------------------

    private static byte[] Tiff(int orientation, bool little = true)
    {
        var bytes = new List<byte>();
        if (little)
        {
            bytes.AddRange(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0 });
        }
        else
        {
            bytes.AddRange(new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0 });
        }
        return bytes.ToArray();
    }

    private static byte[] PngWithChunk(string type, byte[] data)
    {
        var png = Png(4, 2, Colors.Green);
        // After the 8 byte signature and the 25 byte IHDR chunk.
        var chunk = new List<byte>();
        chunk.AddRange(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
        chunk.AddRange(Encoding.ASCII.GetBytes(type));
        chunk.AddRange(data);
        chunk.AddRange(new byte[4]);
        return png.Take(33).Concat(chunk).Concat(png.Skip(33)).ToArray();
    }

    private static byte[] Webp(byte[] exifChunk)
    {
        var body = new List<byte>();
        body.AddRange(Encoding.ASCII.GetBytes("WEBP"));
        body.AddRange(Encoding.ASCII.GetBytes("VP8X"));
        body.AddRange(new byte[] { 10, 0, 0, 0, 0x08, 0, 0, 0, 3, 0, 0, 1, 0, 0 });
        body.AddRange(Encoding.ASCII.GetBytes("EXIF"));
        body.AddRange(BitConverter.GetBytes(exifChunk.Length));
        body.AddRange(exifChunk);
        if (exifChunk.Length % 2 == 1)
        {
            body.Add(0);
        }
        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        file.AddRange(BitConverter.GetBytes(body.Count));
        file.AddRange(body);
        return file.ToArray();
    }

    private static byte[] Jpeg(byte[] tiff)
    {
        var app1 = Encoding.ASCII.GetBytes("Exif").Concat(new byte[] { 0, 0 }).Concat(tiff).ToArray();
        var length = app1.Length + 2;
        return new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length }
            .Concat(app1).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
    }

    [Fact]
    public void AnOrientationIsFoundInEveryContainerPillowReads()
    {
        Assert.True(OrientationProbe.MayBeTurned(PngWithChunk("eXIf", Tiff(6))));
        Assert.True(OrientationProbe.MayBeTurned(PngWithChunk("eXIf", Tiff(3, little: false))));
        Assert.True(OrientationProbe.MayBeTurned(Webp(Tiff(6))));
        Assert.True(OrientationProbe.MayBeTurned(Webp(Encoding.ASCII.GetBytes("Exif\0\0").Concat(Tiff(8)).ToArray())));
        Assert.True(OrientationProbe.MayBeTurned(Tiff(6)));
        Assert.True(OrientationProbe.MayBeTurned(Jpeg(Tiff(6))));
        Assert.True(OrientationProbe.MayBeTurned(Convert.FromBase64String(ReviewPngOrientation6)));
        // XMP, which pillow reads when EXIF has no Orientation.
        Assert.True(OrientationProbe.MayBeTurned(PngWithChunk("iTXt",
            Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta><rdf:Description tiff:Orientation=\"6\"/></x:xmpmeta>"))));
        // A raw EXIF profile in a text chunk is not decoded here: doubt goes to the engine.
        Assert.True(OrientationProbe.MayBeTurned(PngWithChunk("zTXt",
            Encoding.ASCII.GetBytes("Raw profile type exif\0\0xxxx"))));
        // Broken EXIF: doubt goes to the engine.
        Assert.True(OrientationProbe.MayBeTurned(PngWithChunk("eXIf", new byte[] { (byte)'I', (byte)'I', 42, 0, 200, 0, 0, 0 })));
    }

    [Fact]
    public void AnUprightOrUntaggedFileStaysOnTheFastRoad()
    {
        Assert.False(OrientationProbe.MayBeTurned(Png(4, 2, Colors.Green)));
        Assert.False(OrientationProbe.MayBeTurned(PngWithChunk("eXIf", Tiff(1))));
        Assert.False(OrientationProbe.MayBeTurned(Webp(Tiff(1))));
        Assert.False(OrientationProbe.MayBeTurned(Tiff(1)));
        Assert.False(OrientationProbe.MayBeTurned(Jpeg(Tiff(1))));
        Assert.False(OrientationProbe.MayBeTurned(PngWithChunk("iTXt",
            Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta tiff:Orientation=\"1\"/>"))));
        Assert.False(OrientationProbe.MayBeTurned(new byte[] { (byte)'B', (byte)'M', 0, 0 }));
    }

    // The reviewer's PNG (eXIf Orientation 6, stored 40x20).
    private const string ReviewPngOrientation6 =
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAUCAIAAABwJOjsAAAAGmVYSWZNTQAqAAAACAABARIAAwAAAAEABgAAAAAAANZnS2kAAAAkSURBVHic7c0xAQAACAMgtX/nGWMPFGAzHVd6xWKxWCwWiwseMKcBJ2rhLh4AAAAASUVORK5CYII=";

    // --- 6. metadata: an emptied location is removed, bad numbers refused ------

    private const string ViewWithLocation = """
    {"ok": true, "outputs": [], "message": "Read the metadata of meta.jpg", "data": {"metadata": {"file": "meta.jpg", "format": "JPEG", "mode": "RGB", "width": 400, "height": 300, "title": "a title", "author": "an author", "latitude": 32.4567, "longitude": -116.6123, "exif": {"GPSInfo": "6 bytes"}}}}
    """;

    private const string ViewWithoutLocation = """
    {"ok": true, "outputs": [], "message": "Read", "data": {"metadata": {"file": "meta.jpg", "format": "JPEG", "width": 400, "height": 300, "title": "a title", "author": "", "latitude": null, "longitude": null, "exif": {}}}}
    """;

    private static MetadataFields Fields(string view) =>
        MetadataFields.FromView(JsonSerializer.Deserialize<RunResult>(view)!.Data);

    [Fact]
    public void EmptyingTheLocationSendsClearLocation()
    {
        var fields = Fields(ViewWithLocation);
        Assert.True(fields.HadLocation);
        fields.Title = string.Empty;
        fields.Author = string.Empty;
        fields.Latitude = "  ";
        fields.Longitude = string.Empty;
        Assert.True(fields.TrySet(out var parameters, out var problem), problem);
        Assert.Equal("set", parameters["action"]);
        Assert.Equal("true", parameters["clear_location"]);
        Assert.False(parameters.ContainsKey("latitude"));
        Assert.True(ImageEditorTests.Schema("image.metadata").Declares("clear_location"));
    }

    [Fact]
    public void AnEngineWithoutClearLocationRefusesInsteadOfKeepingIt()
    {
        var fields = Fields(ViewWithLocation);
        fields.Latitude = string.Empty;
        fields.Longitude = string.Empty;
        Assert.False(fields.TrySet(out _, out var problem, canClearLocation: false));
        Assert.Contains("Remove every tag", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepingOrChangingTheLocationNeverClearsIt()
    {
        var kept = Fields(ViewWithLocation);
        Assert.True(kept.TrySet(out var same, out _));
        Assert.False(same.ContainsKey("clear_location"));
        Assert.Equal("32.4567", same["latitude"]);

        var none = Fields(ViewWithoutLocation);
        Assert.False(none.HadLocation);
        Assert.True(none.TrySet(out var title, out _));
        Assert.False(title.ContainsKey("clear_location"));
    }

    [Theory]
    [InlineData("NaN", "0")]
    [InlineData("0", "NaN")]
    [InlineData("Infinity", "0")]
    [InlineData("0", "-Infinity")]
    [InlineData("1e400", "0")]
    [InlineData("90.0001", "0")]
    [InlineData("0", "-180.5")]
    public void ANonFiniteOrOutOfRangeCoordinateIsRefusedInTheWindow(string latitude, string longitude)
    {
        var fields = Fields(ViewWithLocation);
        fields.Latitude = latitude;
        fields.Longitude = longitude;
        Assert.False(fields.TrySet(out var parameters, out var problem), JsonSerializer.Serialize(parameters));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    // --- helpers ---------------------------------------------------------------

    private static byte[] Png(int width, int height, Color colour)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = colour.B;
            pixels[i + 1] = colour.G;
            pixels[i + 2] = colour.R;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "peel-round1-6f29", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Cleanup(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
