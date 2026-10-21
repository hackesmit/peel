// Which video and audio entries have a window here, and how each one opens.
//
// EditorHost asks Handles(id) and then OpenAsync, and knows nothing else
// about media. Opening is two reads before any window shows: ffprobe for the
// length and the tags (MediaProbe), and for the two tools that draw a box,
// the engine's own frame to draw it on (FrameGrab). Either failing costs the
// user a toast with the reason, never a window that cannot work.
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Peel.Engine;

namespace Peel.Editors.Timeline;

internal static class MediaEditors
{
    internal static bool Handles(string id) => id is
        "av.video_trim" or "av.video_split" or "av.video_redact" or "av.video_crop"
        or "av.audio_trim" or "av.audio_bleep" or "av.audio_metadata";

    /// <summary>The window for this entry, or (null, the reason). Never throws for a file it cannot read.</summary>
    internal static async Task<(EditorWindowBase? Window, string Problem)> OpenAsync(
        ActionEntry entry, string path, ParamSchema schema, EngineClient engine)
    {
        var (info, problem) = await MediaProbe.ReadAsync(path).ConfigureAwait(true);
        if (info is null)
        {
            return (null, problem);
        }
        var name = Path.GetFileName(path);
        if (entry.Id == "av.audio_metadata")
        {
            return (new AudioMetadataEditor(entry, path, info, schema), string.Empty);
        }
        if (info.Duration <= 0)
        {
            return (null, $"Peel cannot read how long {name} is, so it cannot show its timeline.");
        }
        if (entry.Id.StartsWith("av.video_", System.StringComparison.Ordinal) && !info.HasVideo)
        {
            return (null, $"{name} has no picture to {entry.Label.ToLowerInvariant()}.");
        }
        System.Windows.Media.Imaging.BitmapSource? frame = null;
        if (entry.Id is "av.video_redact" or "av.video_crop")
        {
            (frame, var refused) = await FrameGrab.AtAsync(engine, path, 0, CancellationToken.None).ConfigureAwait(true);
            if (frame is null)
            {
                return (null, $"Peel cannot show a frame of {name}: {refused}");
            }
        }
        return (new TimelineEditor(entry, path, info, schema, engine, frame), string.Empty);
    }
}
