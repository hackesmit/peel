// Adversarial review, round 1, of the image editor windows (hq-plct.10).
//
// Each test reproduces exactly the arithmetic or the parameter building that a
// window does, so a failure here is a failure in the window.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Peel.Editors;
using Peel.Editors.Image;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

public class ReviewImageEditorsTests
{
    // RedactEditor sizes its stage at 900 by 560 and turns a drag into a
    // region with PixelBox.FromDrag(_map.ToImage(from), _map.ToImage(to), ...)
    // (RedactEditor.xaml.cs OnDown and OnUp). A drag that runs off the
    // bottom right corner of the picture must hide the picture to its last
    // row and column: a redaction that rounds inward leaks.
    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(400, 300)]
    [InlineData(20, 10)]
    public void ARedactDragPastTheEdgeHidesTheLastRowAndColumn(int width, int height)
    {
        var map = new PreviewMap(width, height, 900, 560);
        var from = map.ToImage(new Point(0, 0));
        var to = map.ToImage(new Point(map.ViewSize.Width + 50, map.ViewSize.Height + 50));
        var box = PixelBox.FromDrag(from, to, map.ImageWidth, map.ImageHeight);
        Assert.Equal(width, box.X + box.Width);
        Assert.Equal(height, box.Y + box.Height);
    }

    // The same drag ending inside the picture: the pointer at view x = 20.5
    // on a 0.2 scale preview is image x = 102.5, so pixel 102 is under the
    // drag and must be hidden. Rounding has to expand the destroyed area.
    [Fact]
    public void ARedactDragNeverRoundsTheFarEdgeInward()
    {
        var map = new PreviewMap(4000, 3000, 900, 560);
        Assert.True(map.Scale < 0.2 + 1e-9);
        var from = map.ToImage(new Point(10.5, 10.5));
        var endView = new Point(102.5 * map.Scale, 102.5 * map.Scale);
        var to = map.ToImage(endView);
        var box = PixelBox.FromDrag(from, to, map.ImageWidth, map.ImageHeight);
        Assert.True(box.X + box.Width >= 103, $"right edge {box.X + box.Width}, pixel 102 was under the drag");
        Assert.True(box.Y + box.Height >= 103, $"bottom edge {box.Y + box.Height}, pixel 102 was under the drag");
    }

    // Captured from `peel run image.metadata --param action=view` on hqbox
    // (the same capture ImageEditorTests uses).
    private const string ViewWithLocation = """
    {"ok": true, "outputs": [], "message": "Read the metadata of meta.jpg", "data": {"metadata": {"file": "meta.jpg", "format": "JPEG", "mode": "RGB", "width": 400, "height": 300, "title": "a title", "author": "an author", "latitude": 32.4567, "longitude": -116.6123, "exif": {"Artist": "an author", "ImageDescription": "a title", "GPSInfo": "6 bytes"}}}}
    """;

    // The user opens Metadata on a photo that carries a location, empties the
    // latitude and longitude boxes and saves. image.metadata action=set only
    // writes the keys it is given and keeps every other tag (checked on
    // hqbox: set title=b on a file with GPS 32.5,-116.6 reads back with the
    // same GPS). So the window must either refuse or send something that
    // removes the location. Today it sends only the title and the output
    // still says where the photo was taken.
    [Fact]
    public void EmptyingTheLocationBoxesDoesNotSilentlyKeepTheLocation()
    {
        var fields = MetadataFields.FromView(JsonSerializer.Deserialize<RunResult>(ViewWithLocation)!.Data);
        Assert.False(string.IsNullOrWhiteSpace(fields.Latitude));
        fields.Latitude = string.Empty;
        fields.Longitude = string.Empty;
        var ok = fields.TrySet(out var parameters, out _);
        Assert.False(
            ok && !parameters.ContainsKey("latitude"),
            "set was sent without the location the user removed, so the engine keeps the old GPS: "
            + JsonSerializer.Serialize(parameters));
    }

    // A PNG (eXIf chunk) and a WebP (EXIF chunk) whose Orientation is 6.
    // image.load applies the tag for both (pillow ImageOps.exif_transpose:
    // 40x20 stored opens as 20x40). PreviewSource only sends a file to the
    // engine when WPF reports an Orientation other than 1; if WPF does not
    // read the tag for these containers the window shows the stored 40x20
    // picture while the engine crops, annotates or redacts the upright 20x40.
    private const string PngOrientation6 =
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAUCAIAAABwJOjsAAAAGmVYSWZNTQAqAAAACAABARIAAwAAAAEABgAAAAAAANZnS2kAAAAkSURBVHic7c0xAQAACAMgtX/nGWMPFGAzHVd6xWKxWCwWiwseMKcBJ2rhLh4AAAAASUVORK5CYII=";

    [Fact]
    public async Task APngWithAnOrientationTagIsShownTheWayTheEngineOpensIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "peel-review-6f29", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "turned.png");
            File.WriteAllBytes(file, Convert.FromBase64String(PngOrientation6));
            var (preview, problem) = await PreviewSource.LoadAsync(file, new EngineClient(), CancellationToken.None);
            Assert.True(preview is not null, $"no preview: {problem}");
            Assert.Equal(20, preview!.FileWidth);
            Assert.Equal(40, preview.FileHeight);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
