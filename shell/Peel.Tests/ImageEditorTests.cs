// The image editors, tested where they can be tested without a desktop.
//
// Every window here is a thin shell over one of these classes, and these are
// where the arithmetic that can quietly ruin a photo lives: the mapping from a
// click to an image pixel, the crop box that has to stay inside the picture,
// the parameter JSON the engine parses, and the live preview that claims to be
// pillow's own maths. The scripted UI Automation run on George drives the
// windows; this pins what they are built on.
//
// The entries come from real `peel options` output for a PNG, so a renamed
// parameter or a widened range fails here instead of on George.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Peel.Editors;
using Peel.Editors.Image;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

public class ImageEditorTests
{
    // Captured from `peel options sample.png` on hqbox, 2026-10-19.
    private const string ImageTools = """
    {"ok": true, "files": ["C:\\photos\\sample.png"], "wheels": [
      {"family": "tool", "label": "File tools", "default_modifier": "Ctrl", "groups": [
        {"group": "Images", "entries": [
          {"id": "image.crop", "family": "tool", "label": "Crop", "group": "Images", "inputs": ["jpg", "jpeg", "png", "webp", "heic", "heif", "tiff", "tif", "avif", "bmp"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "x", "type": "int", "label": "Left", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "y", "type": "int", "label": "Top", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "w", "type": "int", "label": "Width", "default": null, "required": true, "choices": [], "minimum": 1, "maximum": null, "help": ""}, {"name": "h", "type": "int", "label": "Height", "default": null, "required": true, "choices": [], "minimum": 1, "maximum": null, "help": ""}], "backends": ["pillow"], "missing_backends": [], "available": true},
          {"id": "image.edit", "family": "tool", "label": "Edit", "group": "Images", "inputs": ["jpg", "jpeg", "png", "webp", "heic", "heif", "tiff", "tif", "avif", "bmp"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": null, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "rotate", "type": "enum", "label": "Rotate", "default": 0, "required": false, "choices": [0, 90, 180, 270], "minimum": null, "maximum": null, "help": "Degrees clockwise."}, {"name": "flip", "type": "enum", "label": "Flip", "default": "none", "required": false, "choices": ["none", "horizontal", "vertical"], "minimum": null, "maximum": null, "help": ""}, {"name": "brightness", "type": "float", "label": "Brightness", "default": 1.0, "required": false, "choices": [], "minimum": 0.0, "maximum": 4.0, "help": ""}, {"name": "contrast", "type": "float", "label": "Contrast", "default": 1.0, "required": false, "choices": [], "minimum": 0.0, "maximum": 4.0, "help": ""}, {"name": "saturation", "type": "float", "label": "Saturation", "default": 1.0, "required": false, "choices": [], "minimum": 0.0, "maximum": 4.0, "help": ""}], "backends": ["pillow"], "missing_backends": [], "available": true},
          {"id": "image.annotate", "family": "tool", "label": "Annotate", "group": "Images", "inputs": ["jpg", "jpeg", "png", "webp", "heic", "heif", "tiff", "tif", "avif", "bmp"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "shapes", "type": "json", "label": "Shapes", "default": null, "required": true, "choices": [], "minimum": null, "maximum": null, "help": "A list of shapes. pen: points, color, width. arrow: from, to, color, width. rect: x, y, w, h, color, width, fill. text: x, y, text, color, size."}], "backends": ["pillow"], "missing_backends": [], "available": true},
          {"id": "image.redact", "family": "tool", "label": "Redact", "group": "Images", "inputs": ["jpg", "jpeg", "png", "webp", "heic", "heif", "tiff", "tif", "avif", "bmp"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "regions", "type": "json", "label": "Regions", "default": null, "required": true, "choices": [], "minimum": null, "maximum": null, "help": "A list of {x, y, w, h} rectangles to hide."}, {"name": "mode", "type": "enum", "label": "Hide with", "default": "blur", "required": false, "choices": ["blur", "box"], "minimum": null, "maximum": null, "help": ""}], "backends": ["pillow"], "missing_backends": [], "available": true},
          {"id": "image.metadata", "family": "tool", "label": "Metadata", "group": "Images", "inputs": ["jpg", "jpeg", "png", "webp", "tiff", "tif", "heic", "heif", "avif"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "action", "type": "enum", "label": "Action", "default": "view", "required": false, "choices": ["view", "strip", "set"], "minimum": null, "maximum": null, "help": ""}, {"name": "title", "type": "string", "label": "Title", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "author", "type": "string", "label": "Author", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "latitude", "type": "float", "label": "Latitude", "default": null, "required": false, "choices": [], "minimum": -90.0, "maximum": 90.0, "help": ""}, {"name": "longitude", "type": "float", "label": "Longitude", "default": null, "required": false, "choices": [], "minimum": -180.0, "maximum": 180.0, "help": ""}, {"name": "clear_location", "type": "bool", "label": "Clear location", "default": false, "required": false, "choices": [], "minimum": null, "maximum": null, "help": "With set: remove the GPS location and keep every other tag."}], "backends": ["pillow"], "missing_backends": [], "available": true}]}]}
    ], "warnings": []}
    """;

    private static readonly Dictionary<string, ActionEntry> Entries =
        JsonSerializer.Deserialize<OptionsResult>(
                ImageTools, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Wheels.SelectMany(wheel => wheel.Groups).SelectMany(group => group.Entries)
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);

    private static ActionEntry Entry(string id) => Entries[id];

    internal static ParamSchema Schema(string id) => new(Entry(id));

    // --- the schema the windows are built from -----------------------------

    [Fact]
    public void TheEngineStillDeclaresEveryImageEditor()
    {
        Assert.Equal(5, Entries.Count);
        foreach (var id in new[] { "image.crop", "image.edit", "image.annotate", "image.redact", "image.metadata" })
        {
            Assert.True(Entry(id).NeedsEditor, id);
            Assert.True(EditorHost.Knows(id), id);
        }
        Assert.False(EditorHost.Knows("video.trim"));
    }

    [Fact]
    public void TheSlidersTakeTheirRangeFromTheEngine()
    {
        var schema = Schema("image.edit");
        foreach (var name in new[] { "brightness", "contrast", "saturation" })
        {
            Assert.Equal(0.0, schema.Minimum(name, -1));
            Assert.Equal(4.0, schema.Maximum(name, -1));
            Assert.Equal(1.0, schema.Number(name, -1));
        }
        // An undeclared parameter falls back rather than drawing nothing: an
        // older shell against a newer engine must still open.
        Assert.Equal(7.0, schema.Maximum("gamma", 7.0));
        Assert.False(schema.Declares("gamma"));
    }

    [Fact]
    public void EnumChoicesArriveInTheEnginesOrderAsTheCliSeesThem()
    {
        Assert.Equal(new[] { "0", "90", "180", "270" }, Schema("image.edit").Choices("rotate"));
        Assert.Equal(new[] { "none", "horizontal", "vertical" }, Schema("image.edit").Choices("flip"));
        Assert.Equal(new[] { "blur", "box" }, Schema("image.redact").Choices("mode"));
        Assert.Equal(new[] { "view", "strip", "set" }, Schema("image.metadata").Choices("action"));
        Assert.Equal("Left", Schema("image.crop").Label("x", "fallback"));
    }

    // --- a click to a pixel -------------------------------------------------

    [Fact]
    public void ThePreviewMapsAClickToThePixelUnderIt()
    {
        var map = new PreviewMap(4000, 3000, 900, 600);
        Assert.Equal(0.2, map.Scale, 6);
        Assert.Equal(800, map.ViewSize.Width, 6);
        Assert.Equal(600, map.ViewSize.Height, 6);
        Assert.Equal(new Point(0, 0), map.ToPixel(new Point(0, 0)));
        Assert.Equal(new Point(2000, 1500), map.ToPixel(new Point(400, 300)));
        // Past the edge of the picture, because that is where a drag ends.
        Assert.Equal(new Point(3999, 2999), map.ToPixel(new Point(9999, 9999)));
        Assert.Equal(new Point(0, 0), map.ToPixel(new Point(-50, -50)));
        // As an edge, a drag past the corner reaches the full size.
        Assert.Equal(new Point(4000, 3000), map.ToImage(new Point(9999, 9999)));
        Assert.Equal(new Point(0, 0), map.ToImage(new Point(-50, -50)));
        Assert.Equal(102.5, map.ToImage(new Point(20.5, 0)).X, 6);
        var box = map.ToView(1000, 500, 200, 100);
        Assert.Equal(new Rect(200, 100, 40, 20), box);
    }

    [Fact]
    public void ASmallPictureIsEnlargedButNotWithoutLimit()
    {
        var map = new PreviewMap(20, 10, 900, 600);
        Assert.Equal(PreviewMap.MaxZoom, map.Scale, 6);
        Assert.Equal(160, map.ViewSize.Width, 6);
        var silly = new PreviewMap(0, 0, 0, 0);
        Assert.Equal(1, silly.ImageWidth);
        Assert.Equal(1.0, silly.Scale, 6);
    }

    // --- the crop box -------------------------------------------------------

    [Fact]
    public void ACropStartsAsTheWholePictureAndSaysSo()
    {
        var plan = new CropPlan(400, 300);
        Assert.True(plan.CoversWholeImage);
        Assert.Equal(
            new Dictionary<string, string> { ["x"] = "0", ["y"] = "0", ["w"] = "400", ["h"] = "300" },
            plan.ToParams());
    }

    [Fact]
    public void ACropBoxNeverLeavesThePicture()
    {
        // image.crop raises when x + w is past the width, so every road into
        // the box has to clamp: typed numbers, a drag and a move.
        var plan = new CropPlan(400, 300);
        plan.Set(380, 290, 100, 100);
        Assert.Equal(300, plan.X);
        Assert.Equal(200, plan.Y);
        Assert.Equal(100, plan.Width);
        Assert.Equal(100, plan.Height);

        plan.Set(-20, -20, 500, 500);
        Assert.Equal(0, plan.X);
        Assert.Equal(0, plan.Y);
        Assert.Equal(400, plan.Width);
        Assert.Equal(300, plan.Height);

        plan.Set(10, 10, 0, 0);
        Assert.Equal(1, plan.Width);
        Assert.Equal(1, plan.Height);

        plan.Set(100, 100, 50, 50);
        plan.Begin(CropHandle.Body, new Point(120, 120));
        plan.DragTo(new Point(9999, 9999));
        plan.End();
        Assert.Equal(350, plan.X);
        Assert.Equal(250, plan.Y);
        Assert.Equal(50, plan.Width);
        Assert.Equal(50, plan.Height);
    }

    [Fact]
    public void DraggingACornerResizesFromTheOppositeOne()
    {
        var plan = new CropPlan(400, 300);
        plan.Set(100, 100, 100, 100);
        plan.Begin(CropHandle.BottomRight, new Point(200, 200));
        plan.DragTo(new Point(260, 240));
        plan.End();
        Assert.Equal(100, plan.X);
        Assert.Equal(100, plan.Y);
        Assert.Equal(160, plan.Width);
        Assert.Equal(140, plan.Height);
    }

    [Fact]
    public void ALockedRatioStaysLockedThroughADragAndAClamp()
    {
        var plan = new CropPlan(400, 300);
        plan.SetAspect(1.0);
        Assert.Equal(plan.Width, plan.Height);
        Assert.Equal(300, plan.Width);

        plan.SetAspect(16.0 / 9.0);
        Assert.Equal(16.0 / 9.0, plan.Width / (double)plan.Height, 1);

        plan.Begin(CropHandle.BottomRight, new Point(plan.X + plan.Width, plan.Y + plan.Height));
        plan.DragTo(new Point(9999, 9999));
        plan.End();
        Assert.True(plan.X + plan.Width <= 400);
        Assert.True(plan.Y + plan.Height <= 300);
        Assert.Equal(16.0 / 9.0, plan.Width / (double)plan.Height, 1);
    }

    [Fact]
    public void TheWholeImagePresetComesBack()
    {
        var plan = new CropPlan(400, 300);
        plan.SetAspect(1.0);
        plan.Reset();
        Assert.True(plan.CoversWholeImage);
        Assert.Null(plan.Aspect);
        Assert.Equal("400 by 300 at 0,0", plan.Describe());
    }

    [Fact]
    public void TheRatioPresetsIncludeThePicturesOwn()
    {
        var plan = new CropPlan(1600, 900);
        var original = plan.Aspects.Single(aspect => aspect.Label == "Original");
        Assert.Equal(1600.0 / 900.0, original.Ratio!.Value, 6);
        Assert.Null(plan.Aspects.Single(aspect => aspect.Label == "Free").Ratio);
    }

    // --- the live preview is the engine's own maths -------------------------

    /// <summary>
    /// Four pixels, and what pillow 13 makes of them. Produced on hqbox with
    /// ImageEnhance on an RGB image and pasted in: if Adjust and pillow ever
    /// disagree, the Edit window is lying about what it is about to write.
    /// </summary>
    private static byte[] Four() => new byte[]
    {
        30, 20, 10, 255,
        50, 100, 200, 255,
        0, 0, 0, 255,
        255, 255, 255, 255,
    };

    private static (int R, int G, int B, int A) At(byte[] bgra, int index) =>
        (bgra[index * 4 + 2], bgra[index * 4 + 1], bgra[index * 4], bgra[index * 4 + 3]);

    [Fact]
    public void LumaIsPillowsLuma()
    {
        Assert.Equal(18, Adjust.Luma(10, 20, 30));
        Assert.Equal(124, Adjust.Luma(200, 100, 50));
        Assert.Equal(0, Adjust.Luma(0, 0, 0));
        Assert.Equal(255, Adjust.Luma(255, 255, 255));
        Assert.Equal(99, Adjust.MeanLuma(Four(), 4));
    }

    [Fact]
    public void BrightnessMatchesPillow()
    {
        var pixels = Four();
        Adjust.Apply(pixels, 4, 1.5, 1.0, 1.0);
        Assert.Equal((15, 30, 45, 255), At(pixels, 0));
        Assert.Equal((255, 150, 75, 255), At(pixels, 1));
        Assert.Equal((0, 0, 0, 255), At(pixels, 2));
        Assert.Equal((255, 255, 255, 255), At(pixels, 3));
    }

    [Fact]
    public void ContrastMatchesPillow()
    {
        var pixels = Four();
        Adjust.Apply(pixels, 4, 1.0, 0.5, 1.0);
        Assert.Equal((54, 59, 64, 255), At(pixels, 0));
        Assert.Equal((149, 99, 74, 255), At(pixels, 1));
        Assert.Equal((49, 49, 49, 255), At(pixels, 2));
        Assert.Equal((177, 177, 177, 255), At(pixels, 3));
    }

    [Fact]
    public void SaturationMatchesPillow()
    {
        var pixels = Four();
        Adjust.Apply(pixels, 4, 1.0, 1.0, 2.0);
        Assert.Equal((2, 22, 42, 255), At(pixels, 0));
        Assert.Equal((255, 76, 0, 255), At(pixels, 1));
        Assert.Equal((0, 0, 0, 255), At(pixels, 2));
        Assert.Equal((255, 255, 255, 255), At(pixels, 3));
    }

    [Fact]
    public void TheThreeTogetherMatchPillowInTheEnginesOrder()
    {
        // families/image.py enhances brightness, then contrast, then
        // saturation, and the contrast midpoint is the mean of the already
        // brightened image. Doing them in another order gives other numbers.
        var pixels = Four();
        Adjust.Apply(pixels, 4, 1.5, 0.5, 2.0);
        Assert.Equal((58, 74, 88, 255), At(pixels, 0));
        Assert.Equal((225, 121, 45, 255), At(pixels, 1));
        Assert.Equal((57, 57, 57, 255), At(pixels, 2));
        Assert.Equal((184, 184, 184, 255), At(pixels, 3));
    }

    [Fact]
    public void AnEnhancerLeavesTheAlphaChannelAlone()
    {
        // pillow copies the source alpha into its degenerate image, so a
        // translucent PNG does not change opacity when it is dimmed.
        var pixels = new byte[] { 30, 20, 10, 128, 255, 255, 255, 0 };
        Adjust.Apply(pixels, 2, 0.5, 0.5, 0.5);
        Assert.Equal(128, pixels[3]);
        Assert.Equal(0, pixels[7]);
    }

    [Fact]
    public void ANeutralFactorIsNotSentToTheEngine()
    {
        var plan = new EditPlan(Schema("image.edit"));
        Assert.Empty(plan.ToParams());
        Assert.False(plan.AnyChange);

        plan.Brightness = 1.25;
        plan.Rotate = "90";
        plan.Flip = "vertical";
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["rotate"] = "90",
                ["flip"] = "vertical",
                ["brightness"] = "1.25",
            },
            plan.ToParams());
        Assert.Equal(90, plan.RotateDegrees);
        Assert.True(plan.FlipsVertically);

        plan.Reset();
        Assert.Empty(plan.ToParams());
    }

    [Fact]
    public void AFactorIsWrittenTheWayPythonsFloatReadsIt()
    {
        var plan = new EditPlan(Schema("image.edit"));
        plan.Saturation = 0.0;
        plan.Contrast = 3.333333;
        var parameters = plan.ToParams();
        Assert.Equal("0", parameters["saturation"]);
        Assert.Equal("3.33", parameters["contrast"]);
        // Never a comma, whatever the machine's locale says a decimal point is.
        Assert.DoesNotContain(",", parameters["contrast"]);
    }

    [Fact]
    public void AFactorPastTheEnginesRangeIsPulledBackIntoIt()
    {
        var plan = new EditPlan(Schema("image.edit"));
        plan.Brightness = 99;
        Assert.Equal("4", plan.ToParams()["brightness"]);
    }

    // --- the shapes and regions the engine parses --------------------------

    [Fact]
    public void EachShapeCarriesTheKeysTheEngineAsksFor()
    {
        var plan = new AnnotatePlan();
        plan.Add(new ShapeSpec
        {
            Kind = ShapeKind.Pen,
            Points = new[] { new Point(1, 2), new Point(3, 4) },
            Colour = "#ff3b30",
            Width = 5,
        });
        plan.Add(new ShapeSpec { Kind = ShapeKind.Arrow, From = new Point(10, 10), To = new Point(90, 40), Width = 4 });
        plan.Add(new ShapeSpec { Kind = ShapeKind.Rect, Box = new Int32Rect(5, 6, 70, 80), Width = 3 });
        plan.Add(new ShapeSpec { Kind = ShapeKind.Text, From = new Point(20, 30), Text = "a \"quoted\" note", FontSize = 24 });

        using var shapes = JsonDocument.Parse(plan.ToJson());
        var list = shapes.RootElement.EnumerateArray().ToList();
        Assert.Equal(4, list.Count);

        Assert.Equal("pen", list[0].GetProperty("type").GetString());
        Assert.Equal("#ff3b30", list[0].GetProperty("color").GetString());
        Assert.Equal(5, list[0].GetProperty("width").GetInt32());
        Assert.Equal(new[] { 1, 2 }, list[0].GetProperty("points")[0].EnumerateArray().Select(n => n.GetInt32()));

        Assert.Equal("arrow", list[1].GetProperty("type").GetString());
        Assert.Equal(new[] { 10, 10 }, list[1].GetProperty("from").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Equal(new[] { 90, 40 }, list[1].GetProperty("to").EnumerateArray().Select(n => n.GetInt32()));

        Assert.Equal("rect", list[2].GetProperty("type").GetString());
        Assert.Equal(5, list[2].GetProperty("x").GetInt32());
        Assert.Equal(6, list[2].GetProperty("y").GetInt32());
        Assert.Equal(70, list[2].GetProperty("w").GetInt32());
        Assert.Equal(80, list[2].GetProperty("h").GetInt32());
        Assert.False(list[2].TryGetProperty("fill", out _));

        Assert.Equal("text", list[3].GetProperty("type").GetString());
        // A caption with a quote in it is JSON, not string concatenation.
        Assert.Equal("a \"quoted\" note", list[3].GetProperty("text").GetString());
        Assert.Equal(24, list[3].GetProperty("size").GetInt32());
    }

    [Fact]
    public void UndoTakesTheLastShapeAndClearTakesThemAll()
    {
        var plan = new AnnotatePlan();
        Assert.False(plan.Any);
        Assert.Null(plan.Undo());
        plan.Add(new ShapeSpec { Kind = ShapeKind.Rect, Box = new Int32Rect(0, 0, 10, 10) });
        plan.Add(new ShapeSpec { Kind = ShapeKind.Rect, Box = new Int32Rect(20, 20, 10, 10) });
        Assert.Equal(20, plan.Undo()!.Box.X);
        Assert.Single(plan.Shapes);
        plan.Clear();
        Assert.False(plan.Any);
    }

    [Fact]
    public void ARegionIsTheBoxTheUserDraggedWhicheverWayRound()
    {
        var box = PixelBox.FromDrag(new Point(300, 220), new Point(100, 60), 400, 300);
        Assert.Equal(new Int32Rect(100, 60, 200, 160), box);
        // Clipped to the picture, because the engine drops a region that does
        // not overlap it and clamps the rest.
        var over = PixelBox.FromDrag(new Point(-50, -50), new Point(9999, 9999), 400, 300);
        Assert.Equal(new Int32Rect(0, 0, 400, 300), over);
        // Never without area: image.redact refuses w or h below one.
        var dot = PixelBox.FromDrag(new Point(10, 10), new Point(10, 10), 400, 300);
        Assert.Equal(1, dot.Width);
        Assert.Equal(1, dot.Height);
    }

    [Fact]
    public void RedactTakesItsWaysOfHidingFromTheEngine()
    {
        var plan = new RedactPlan(Schema("image.redact"));
        Assert.Equal(new[] { "blur", "box" }, plan.Modes);
        Assert.Equal("blur", plan.Mode);
        plan.Mode = "box";
        plan.Add(new Int32Rect(10, 20, 30, 40));
        var parameters = plan.ToParams();
        Assert.Equal("box", parameters["mode"]);
        using var regions = JsonDocument.Parse(parameters["regions"]);
        var region = regions.RootElement.EnumerateArray().Single();
        Assert.Equal(10, region.GetProperty("x").GetInt32());
        Assert.Equal(20, region.GetProperty("y").GetInt32());
        Assert.Equal(30, region.GetProperty("w").GetInt32());
        Assert.Equal(40, region.GetProperty("h").GetInt32());
        Assert.Equal("1 region, box", plan.Describe());
    }

    // --- metadata ----------------------------------------------------------

    // Captured from `peel run image.metadata --param action=view` on hqbox.
    private const string RealView = """
    {"ok": true, "outputs": [], "message": "Read the metadata of meta.jpg", "data": {"metadata": {"file": "meta.jpg", "format": "JPEG", "mode": "RGB", "width": 400, "height": 300, "title": "a title", "author": "an author", "latitude": 32.4567, "longitude": -116.6123, "exif": {"Artist": "an author", "ImageDescription": "a title", "GPSInfo": "6 bytes"}}}}
    """;

    private static MetadataFields Read(string json) =>
        MetadataFields.FromView(JsonSerializer.Deserialize<RunResult>(json)!.Data);

    [Fact]
    public void TheFormIsFilledFromWhatTheEngineRead()
    {
        var fields = Read(RealView);
        Assert.Equal("meta.jpg", fields.File);
        Assert.Equal("JPEG", fields.Format);
        Assert.Equal(400, fields.Width);
        Assert.Equal(300, fields.Height);
        Assert.Equal("a title", fields.Title);
        Assert.Equal("an author", fields.Author);
        Assert.Equal("32.4567", fields.Latitude);
        Assert.Equal("-116.6123", fields.Longitude);
        Assert.Equal(3, fields.Tags.Count);
        Assert.Equal("Artist", fields.Tags[0].Key);
    }

    [Fact]
    public void APhotoWithNoTagsAtAllIsNotAnError()
    {
        var fields = Read("""{"ok": true, "outputs": [], "message": "", "data": {"metadata": {"file": "bare.png", "format": "PNG", "mode": "RGB", "width": 8, "height": 8, "title": null, "author": null, "latitude": null, "longitude": null, "exif": {}}}}""");
        Assert.Equal("bare.png", fields.File);
        Assert.Equal(string.Empty, fields.Title);
        Assert.Equal(string.Empty, fields.Latitude);
        Assert.Empty(fields.Tags);
    }

    [Fact]
    public void SavingNeedsSomethingToSave()
    {
        var fields = new MetadataFields();
        Assert.False(fields.TrySet(out _, out var problem));
        Assert.Contains("title", problem);
    }

    [Fact]
    public void ALocationNeedsBothHalves()
    {
        var fields = new MetadataFields { Latitude = "32.4" };
        Assert.False(fields.TrySet(out _, out var problem));
        Assert.Contains("both", problem);

        fields.Longitude = "-116.6";
        Assert.True(fields.TrySet(out var parameters, out _));
        Assert.Equal("32.4", parameters["latitude"]);
        Assert.Equal("-116.6", parameters["longitude"]);
        Assert.Equal("set", parameters["action"]);
    }

    [Fact]
    public void AnUnreadableNumberIsSaidSoRatherThanSent()
    {
        var fields = new MetadataFields { Latitude = "north", Longitude = "west" };
        Assert.False(fields.TrySet(out _, out var problem));
        Assert.Contains("north", problem);

        var tooFar = new MetadataFields { Latitude = "200", Longitude = "0" };
        Assert.False(tooFar.TrySet(out _, out var range));
        Assert.Contains("-90", range);
    }

    [Fact]
    public void StripAndViewAreTheEnginesOwnWords()
    {
        Assert.Equal("strip", MetadataFields.StripParams()["action"]);
        Assert.Equal("view", MetadataFields.ViewParams()["action"]);
        Assert.Contains("strip", Schema("image.metadata").Choices("action"));
    }

    // --- getting the parameters to the engine ------------------------------

    [Fact]
    public void ShortParametersGoOnTheCommandLine()
    {
        var tail = new List<string>();
        var file = EngineClient.ParamArgs(
            new Dictionary<string, string> { ["x"] = "10", ["y"] = "20" }, 6000, tail);
        Assert.Null(file);
        Assert.Equal(new[] { "--param", "x=10", "--param", "y=20" }, tail);
    }

    [Fact]
    public void NoParametersMeanNoArguments()
    {
        var tail = new List<string>();
        Assert.Null(EngineClient.ParamArgs(null, 6000, tail));
        Assert.Null(EngineClient.ParamArgs(new Dictionary<string, string>(), 6000, tail));
        Assert.Empty(tail);
    }

    [Fact]
    public void AValueStartingWithAnAtSignGoesInAFile()
    {
        // `peel run --param K=@path` reads the value from that file, so a
        // Metadata title of "@danielfhack" would be read as a file name and
        // the job would fail. The JSON object has no magic values.
        var tail = new List<string>();
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = EngineClient.ParamArgs(
            new Dictionary<string, string> { ["action"] = "set", ["title"] = "@danielfhack" },
            6000,
            tail,
            (path, contents) => written[path] = contents);
        Assert.NotNull(file);
        Assert.Equal(new[] { "--params-file", file! }, tail);
        Assert.EndsWith(".json", file);
        using var parsed = JsonDocument.Parse(written[file!]);
        Assert.Equal("@danielfhack", parsed.RootElement.GetProperty("title").GetString());
        Assert.Equal("set", parsed.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public void ALongShapeListGoesInAFileToo()
    {
        var shapes = new AnnotatePlan();
        for (var i = 0; i < 400; i++)
        {
            shapes.Add(new ShapeSpec { Kind = ShapeKind.Rect, Box = new Int32Rect(i, i, 10, 10) });
        }
        var tail = new List<string>();
        var written = string.Empty;
        var file = EngineClient.ParamArgs(shapes.ToParams(), 6000, tail, (_, contents) => written = contents);
        Assert.NotNull(file);
        Assert.Equal("--params-file", tail[0]);
        using var parsed = JsonDocument.Parse(written);
        using var inner = JsonDocument.Parse(parsed.RootElement.GetProperty("shapes").GetString()!);
        Assert.Equal(400, inner.RootElement.GetArrayLength());
    }

    [Fact]
    public void AHalfWrittenParametersFileIsNeverLeftBehind()
    {
        var tail = new List<string>();
        string? attempted = null;
        Assert.Throws<IOException>(() => EngineClient.ParamArgs(
            new Dictionary<string, string> { ["title"] = "@x" },
            6000,
            tail,
            (path, _) =>
            {
                attempted = path;
                File.WriteAllText(path, "half");
                throw new IOException("the disk is full");
            }));
        Assert.NotNull(attempted);
        Assert.False(File.Exists(attempted!));
    }
}
