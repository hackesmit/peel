// The picture an editor shows, and the pixels the engine will work on.
//
// Two traps live here, and both silently crop or redact the wrong part of a
// photo rather than failing:
//
//  1. Orientation. The engine opens every image upright (image.load applies
//     and drops the EXIF Orientation tag) and its docstring says a crop box
//     from the editor refers to what the editor showed. WPF does not apply the
//     tag, so a phone photo would be shown on its side while the engine works
//     on it upright. Rather than reimplement the eight transposes and hope WPF
//     never starts applying them too, any file whose Orientation is not 1 is
//     rasterised by the engine itself: the preview then IS what the engine
//     sees, by construction.
//  2. Formats. WPF cannot decode HEIC or AVIF without a Store codec, and the
//     shell is not allowed to know about formats anyway. The same engine
//     rasterising covers it, into a temporary folder (--out-dir) so no file
//     ever lands next to the user's.
//
// Coordinates are always the file's own pixels. The bitmap may be smaller than
// the file (a 50 megapixel photo is pointless in a 900 pixel window), so the
// window asks PreviewMap for the mapping and never measures the bitmap.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Peel.Engine;

namespace Peel.Editors;

internal sealed class PreviewSource
{
    /// <summary>Longest side of the preview bitmap. A window is never bigger.</summary>
    internal const int LongestSide = 1600;

    private PreviewSource(BitmapSource bitmap, int fileWidth, int fileHeight, byte[] pixels, string how)
    {
        Bitmap = bitmap;
        FileWidth = fileWidth;
        FileHeight = fileHeight;
        Pixels = pixels;
        How = how;
    }

    internal BitmapSource Bitmap { get; }

    /// <summary>The upright size of the file, which is what the engine measures.</summary>
    internal int FileWidth { get; }

    internal int FileHeight { get; }

    /// <summary>Bgra32, preview sized, for the Edit window's live adjustment.</summary>
    internal byte[] Pixels { get; }

    internal int PixelWidth => Bitmap.PixelWidth;

    internal int PixelHeight => Bitmap.PixelHeight;

    /// <summary>For the log: which of the two roads the preview came down.</summary>
    internal string How { get; }

    /// <summary>
    /// Load one file for editing. Never throws: a failure comes back as null
    /// with the reason, because the caller has to tell the user, not crash.
    /// </summary>
    internal static async Task<(PreviewSource? Preview, string Problem)> LoadAsync(
        string path, EngineClient engine, CancellationToken token)
    {
        try
        {
            var direct = await Task.Run(() => Decode(path), token).ConfigureAwait(false);
            if (direct is not null)
            {
                return (direct, string.Empty);
            }
        }
        catch (Exception error)
        {
            Log.Write($"preview: WPF cannot read {path}", error);
        }
        return await ViaEngineAsync(path, engine, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Decode with WPF, or return null when the file has to go to the engine
    /// (a rotated photo, or a format WPF has no codec for).
    /// </summary>
    private static PreviewSource? Decode(string path, string how = "WPF codec")
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            return null;
        }
        var frame = decoder.Frames[0];
        if (Orientation(frame) is not 0 and not 1)
        {
            // Upright is the engine's job, not a transform guessed here.
            return null;
        }
        return Finish(frame, frame.PixelWidth, frame.PixelHeight, how);
    }

    private static async Task<(PreviewSource?, string)> ViaEngineAsync(
        string path, EngineClient engine, CancellationToken token)
    {
        // A PNG source cannot ask for PNG (the registry drops a conversion to
        // the format the file already is), so it asks for BMP, which every
        // Windows codec reads.
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var action = ext == "png" ? "image.to_bmp" : "image.to_png";
        string? folder = null;
        try
        {
            folder = Path.Combine(Path.GetTempPath(), "Peel", $"preview-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var result = await engine.RunAsync(action, new[] { path }, null, token, folder).ConfigureAwait(false);
            if (!result.Ok || result.Outputs.Count == 0)
            {
                var message = result.Message.Length > 0 ? result.Message : "the engine could not read it";
                return (null, $"Peel cannot show {Path.GetFileName(path)}: {message}");
            }
            var rendered = result.Outputs[0];
            var preview = await Task.Run(() => Decode(rendered, "engine raster"), token).ConfigureAwait(false);
            return preview is null
                ? (null, $"Peel cannot show {Path.GetFileName(path)}.")
                : (preview, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (null, "cancelled");
        }
        catch (Exception error)
        {
            Log.Write($"preview: the engine could not rasterise {path}", error);
            return (null, $"Peel cannot show {Path.GetFileName(path)}: {error.Message}");
        }
        finally
        {
            Discard(folder);
        }
    }

    /// <summary>
    /// Scale to preview size, convert to Bgra32 once, and keep both the bitmap
    /// and its bytes: the Edit window needs the bytes on every slider move.
    /// </summary>
    private static PreviewSource Finish(BitmapSource frame, int fileWidth, int fileHeight, string how)
    {
        BitmapSource source = frame;
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > LongestSide)
        {
            var scale = LongestSide / (double)longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new PreviewSource(converted, fileWidth, fileHeight, pixels, how);
    }

    /// <summary>
    /// The EXIF Orientation tag, or 0 when the file has none. Reading metadata
    /// is allowed to fail: a format with no metadata query support throws.
    /// </summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata)
            {
                return 0;
            }
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    if (metadata.GetQuery(query) is { } value)
                    {
                        return Convert.ToInt32(value);
                    }
                }
                catch (Exception)
                {
                    // Not every container answers every query. Try the next.
                }
            }
        }
        catch (Exception error)
        {
            Log.Write("preview: cannot read the orientation tag", error);
        }
        return 0;
    }

    private static void Discard(string? folder)
    {
        if (folder is null)
        {
            return;
        }
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error)
        {
            Log.Write($"preview: cannot delete the temporary folder {folder}", error);
        }
    }
}
