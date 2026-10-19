// Review round 2 checks of the image editor windows (hq-plct.10), written by
// the round 2 reviewer. The orientation matrix needs pillow: it runs the
// python next to PEEL_CLI (a venv with this branch's engine) to generate its
// fixtures, so no binary is committed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Peel.Editors;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

public class ReviewImageEditorsRound2Tests
{
    private const string Generator = """
import sys, json
from pathlib import Path
from PIL import Image, ImageOps, PngImagePlugin
out = Path(sys.argv[1]); out.mkdir(exist_ok=True)
manifest = {}
def base():
    im = Image.new("RGB", (40, 20), (0, 200, 0))
    for x in range(10):
        for y in range(10):
            im.putpixel((x, y), (255, 0, 0))
    return im
def record(name):
    with Image.open(out / name) as im:
        im.load()
        up = ImageOps.exif_transpose(im)
        r, g, b = up.convert("RGB").getpixel((0, 0))
        manifest[name] = [up.size[0], up.size[1], 1 if r > 128 and g < 128 else 0]
for o in range(1, 9):
    for fmt, ext in (("JPEG", "jpg"), ("PNG", "png"), ("WEBP", "webp"), ("TIFF", "tif")):
        im = base(); ex = Image.Exif(); ex[0x0112] = o
        name = f"o{o}.{ext}"
        kw = {"exif": ex.tobytes()}
        if fmt == "TIFF": kw = {"tiffinfo": {274: o}}
        if fmt == "WEBP": kw["lossless"] = True
        im.save(out / name, fmt, **kw); record(name)
xmp = ('<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
       '<rdf:Description xmlns:tiff="http://ns.adobe.com/tiff/1.0/" tiff:Orientation="6"/></rdf:RDF></x:xmpmeta>')
for comp in (False, True):
    info = PngImagePlugin.PngInfo(); info.add_itxt("XML:com.adobe.xmp", xmp, zip=comp)
    name = f"xmp6-itxt-{'z' if comp else 'plain'}.png"
    base().save(out / name, "PNG", pnginfo=info); record(name)
info = PngImagePlugin.PngInfo(); info.add_text("XML:com.adobe.xmp", xmp, zip=True)
base().save(out / "xmp6-ztxt.png", "PNG", pnginfo=info); record("xmp6-ztxt.png")
(out / "manifest.json").write_text(json.dumps(manifest))
""";

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "peel-review2-" + Guid.NewGuid().ToString("N"));
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

    private static Dictionary<string, int[]> Generate(string folder)
    {
        var cli = Environment.GetEnvironmentVariable("PEEL_CLI");
        Assert.False(string.IsNullOrEmpty(cli), "set PEEL_CLI to a venv peel.exe with this branch's engine");
        var python = Path.Combine(Path.GetDirectoryName(cli!)!, "python.exe");
        var script = Path.Combine(folder, "gen.py");
        File.WriteAllText(script, Generator);
        var fixtures = Path.Combine(folder, "fx");
        using var process = Process.Start(new ProcessStartInfo(python, $"-I \"{script}\" \"{fixtures}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return JsonSerializer.Deserialize<Dictionary<string, int[]>>(
            File.ReadAllText(Path.Combine(fixtures, "manifest.json")))!;
    }

    // Round 1 B3 with variants: every EXIF orientation 1 to 8 in JPEG, PNG,
    // WebP and TIFF, plus an XMP tiff:Orientation stored plain and compressed.
    // The preview must be the size and the way round that pillow opens it in,
    // or a crop, an annotation or a redaction lands on another part of the
    // picture.
    [Fact]
    public async Task EveryPreviewIsTheWayRoundTheEngineOpensTheFile()
    {
        var folder = Folder();
        try
        {
            var manifest = Generate(folder);
            var wrong = new List<string>();
            foreach (var (name, want) in manifest.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var file = Path.Combine(folder, "fx", name);
                var (preview, problem) = await PreviewSource.LoadAsync(file, new EngineClient(), CancellationToken.None);
                if (preview is null)
                {
                    wrong.Add($"{name}: no preview ({problem})");
                    continue;
                }
                var p = preview.Pixels;
                var red = p[2] > 128 && p[1] < 128 ? 1 : 0;
                if (preview.FileWidth != want[0] || preview.FileHeight != want[1] || red != want[2])
                {
                    wrong.Add($"{name} via {preview.How}: preview {preview.FileWidth}x{preview.FileHeight} red corner {red}, engine {want[0]}x{want[1]} red corner {want[2]}");
                }
            }
            Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // A file touched (same bytes, new modification time) is the same picture:
    // the identity check must not refuse it. One changed byte must be refused.
    [Fact]
    public async Task ATouchedFileIsAcceptedAndAChangedOneIsRefused()
    {
        var folder = Folder();
        try
        {
            var file = Path.Combine(folder, "same.png");
            File.WriteAllBytes(file, Round1Png());
            var (preview, problem) = await PreviewSource.LoadAsync(file, new EngineClient(), CancellationToken.None);
            Assert.True(preview is not null, problem);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(3));
            File.WriteAllBytes(file, File.ReadAllBytes(file));
            Assert.True(preview!.StillMatches(file, out var why), why);

            var bytes = File.ReadAllBytes(file);
            bytes[^20] ^= 0x01; // same size, other bytes
            File.WriteAllBytes(file, bytes);
            Assert.False(preview.StillMatches(file, out _));
        }
        finally
        {
            Cleanup(folder);
        }
    }

    private static byte[] Round1Png()
    {
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)200);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            4, 3, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    // Drags in all four corners and both directions, on odd sizes, at every
    // zoom the window can reach: the region always reaches the edge the drag
    // passed, and never draws smaller than what is sent.
    [Theory]
    [InlineData(1, 600)]
    [InlineData(600, 1)]
    [InlineData(3, 3)]
    [InlineData(400, 300)]
    [InlineData(8001, 4999)]
    public void CornerDragsReachEveryEdgeInEitherDirection(int width, int height)
    {
        var map = new PreviewMap(width, height, 900, 560);
        var view = map.ViewSize;
        var corners = new[]
        {
            new Point(-50, -50), new Point(view.Width + 50, -50),
            new Point(-50, view.Height + 50), new Point(view.Width + 50, view.Height + 50),
        };
        var centre = new Point(view.Width / 2, view.Height / 2);
        foreach (var corner in corners)
        {
            foreach (var (a, b) in new[] { (corner, centre), (centre, corner) })
            {
                var region = PixelBox.Covering(map, map.ToImage(a), map.ToImage(b));
                Assert.True(region.X >= 0 && region.Y >= 0
                    && region.X + region.Width <= width && region.Y + region.Height <= height, $"{region} leaves {width}x{height}");
                if (corner.X < 0) Assert.Equal(0, region.X);
                if (corner.Y < 0) Assert.Equal(0, region.Y);
                if (corner.X > view.Width) Assert.Equal(width, region.X + region.Width);
                if (corner.Y > view.Height) Assert.Equal(height, region.Y + region.Height);
            }
        }
        var whole = PixelBox.Covering(map, map.ToImage(corners[3]), map.ToImage(corners[0]));
        Assert.Equal(new Int32Rect(0, 0, width, height), whole);
    }
}
