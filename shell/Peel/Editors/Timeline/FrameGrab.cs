// Frames of a video, taken by the engine.
//
// The pictures under the timeline and behind the redact box come from
// `peel run av.video_snapshot --out-dir <temp>`, not from MediaElement, for the
// reason PreviewSource gives for images: the frame is then what the engine
// sees, by construction. A phone clip stored turned is upright in the PNG
// (ffmpeg applies the display matrix), and the PNG's size is the frame size
// av.video_redact checks a rectangle against, so a box drawn on it needs no
// further arithmetic. It also covers every container ffmpeg reads, where
// MediaElement covers what Media Foundation happens to have codecs for.
//
// Each call writes into a folder of its own under %TEMP%\Peel, decodes the PNGs
// into frozen bitmaps and deletes the folder before returning, so nothing is
// ever left next to the user's file or in the temp folder.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Peel.Engine;

namespace Peel.Editors.Timeline;

internal sealed record Frame(double Seconds, BitmapSource Bitmap);

internal static class FrameGrab
{
    internal const string Action = "av.video_snapshot";

    /// <summary>
    /// The interval that gives about <paramref name="count"/> frames over the
    /// file. av.video_snapshot refuses an interval below 0.1 s.
    /// </summary>
    internal static double StripInterval(double duration, int count) =>
        Math.Max(0.1, Math.Round(duration / Math.Max(1, count), 3, MidpointRounding.AwayFromZero));

    /// <summary>
    /// About <paramref name="count"/> frames spread over the file, each
    /// decoded no taller than <paramref name="height"/>. Empty on any failure:
    /// a strip is a help, and the window works without one.
    /// </summary>
    internal static async Task<IReadOnlyList<Frame>> StripAsync(
        EngineClient engine, string path, double duration, int count, int height, CancellationToken token)
    {
        var every = StripInterval(duration, count);
        var parameters = new Dictionary<string, string>
        {
            ["every"] = every.ToString("0.###", CultureInfo.InvariantCulture),
        };
        var frames = await RunAsync(engine, path, parameters, height, token).ConfigureAwait(false);
        return frames.Bitmaps.Select((bitmap, index) => new Frame(index * every, bitmap)).ToList();
    }

    /// <summary>One frame at full size, or (null, the reason).</summary>
    internal static async Task<(BitmapSource? Bitmap, string Problem)> AtAsync(
        EngineClient engine, string path, double seconds, CancellationToken token)
    {
        var parameters = new Dictionary<string, string> { ["time"] = Timecode.Engine(seconds) };
        var frames = await RunAsync(engine, path, parameters, 0, token).ConfigureAwait(false);
        return frames.Bitmaps.Count > 0 ? (frames.Bitmaps[0], string.Empty) : (null, frames.Problem);
    }

    private static async Task<(IReadOnlyList<BitmapSource> Bitmaps, string Problem)> RunAsync(
        EngineClient engine, string path, IReadOnlyDictionary<string, string> parameters, int height,
        CancellationToken token)
    {
        string? folder = null;
        try
        {
            folder = Path.Combine(Path.GetTempPath(), "Peel", $"frames-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var result = await engine.RunAsync(Action, new[] { path }, parameters, token, folder).ConfigureAwait(false);
            if (!result.Ok || result.Outputs.Count == 0)
            {
                var message = result.Message.Length > 0 ? result.Message : "the engine wrote no frame";
                Log.Write($"frames: {Action} for {path} failed: {message}");
                return (Array.Empty<BitmapSource>(), message);
            }
            var decoded = await Task.Run(() => result.Outputs.Select(output => Decode(output, height)).ToList(), token)
                .ConfigureAwait(false);
            return (decoded, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (Array.Empty<BitmapSource>(), "cancelled");
        }
        catch (Exception error)
        {
            Log.Write($"frames: cannot take frames of {path}", error);
            return (Array.Empty<BitmapSource>(), error.Message);
        }
        finally
        {
            Discard(folder);
        }
    }

    private static BitmapSource Decode(string png, int height)
    {
        // A stream, not a Uri: the output is named after the user's file, and
        // a "#" or "%" in a name is a fragment or an escape to a Uri.
        using var stream = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        // OnLoad reads the whole file now, so the folder can go right after.
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (height > 0)
        {
            bitmap.DecodePixelHeight = height;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
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
            Log.Write($"frames: cannot delete the temporary folder {folder}", error);
        }
    }
}
