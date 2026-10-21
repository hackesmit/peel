// How long a media file is, and what it says about itself, before the window opens.
//
// The timeline needs the length before it can draw anything, and the audio
// Metadata form should arrive filled. `peel options` does not say either, and
// there is no engine action that only reads, so the shell asks ffprobe
// directly: the same program the engine probes with (av.probe), found the same
// way (backends.py: PEEL_BACKEND_FFPROBE, then PATH, then the standard Windows
// install folders). Using the same reader is the point: a length the window
// believes and the engine disagrees with would let the user mark a range the
// engine then clamps without saying so.
//
// MediaElement would also report a length, but only for what Media Foundation
// can open. Opus, OGG and MKV are exactly the files it cannot, and those still
// need a timeline.
//
// ffprobe runs like the engine does: created suspended inside a kill on close
// job (ContainedProcess), with a deadline, so a stuck probe never outlives
// Peel. It only reads, so a failure costs a toast and nothing else.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Peel.Jobs;

namespace Peel.Editors.Timeline;

internal sealed class MediaInfo
{
    internal double Duration { get; init; }

    internal bool HasVideo { get; init; }

    internal bool HasAudio { get; init; }

    /// <summary>Tag names lower cased, file tags first, then the audio stream's, first value wins.</summary>
    internal IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal string Tag(string name) => Tags.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>
    /// ffprobe's -show_format -show_streams JSON, read the way av._duration
    /// reads it: the longest of the container's and every stream's duration,
    /// with Matroska's "HH:MM:SS.fff" DURATION tag as a fallback. Never throws;
    /// unreadable JSON is a file of no known length.
    /// </summary>
    internal static MediaInfo Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var best = 0.0;
            var hasVideo = false;
            var hasAudio = false;
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
            {
                best = Math.Max(best, Seconds(format, "duration"));
                AddTags(format, tags);
            }
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var kind) ? kind.GetString() : null;
                    // A cover picture is a video stream with one frame. It is
                    // not a video: an MP3 with artwork is still an audio file.
                    var attached = stream.TryGetProperty("disposition", out var disposition)
                        && disposition.TryGetProperty("attached_pic", out var pic)
                        && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;
                    if (type == "video" && !attached)
                    {
                        hasVideo = true;
                    }
                    else if (type == "audio")
                    {
                        if (!hasAudio)
                        {
                            AddTags(stream, tags);
                        }
                        hasAudio = true;
                    }
                    best = Math.Max(best, Seconds(stream, "duration"));
                    if (stream.TryGetProperty("tags", out var streamTags) && streamTags.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var tag in streamTags.EnumerateObject())
                        {
                            if (string.Equals(tag.Name, "DURATION", StringComparison.OrdinalIgnoreCase)
                                && tag.Value.ValueKind == JsonValueKind.String
                                && Timecode.TryParse(tag.Value.GetString(), out var tagged))
                            {
                                best = Math.Max(best, tagged);
                            }
                        }
                    }
                }
            }
            return new MediaInfo { Duration = best, HasVideo = hasVideo, HasAudio = hasAudio, Tags = tags };
        }
        catch (JsonException)
        {
            return new MediaInfo();
        }
    }

    private static double Seconds(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return 0;
        }
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds > 0
            ? seconds
            : 0;
    }

    private static void AddTags(JsonElement owner, Dictionary<string, string> tags)
    {
        if (!owner.TryGetProperty("tags", out var found) || found.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var tag in found.EnumerateObject())
        {
            var key = tag.Name.ToLowerInvariant();
            if (!tags.ContainsKey(key) && tag.Value.ValueKind == JsonValueKind.String)
            {
                tags[key] = (tag.Value.GetString() ?? string.Empty).Trim();
            }
        }
    }
}

internal static class MediaProbe
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where ffprobe is, looked for in the engine's order, or null. The
    /// folders are backends.py's windows_globs for ffprobe, expanded the same way.
    /// </summary>
    internal static string? Locate(
        Func<string, string?>? environment = null,
        Func<string, bool>? exists = null,
        Func<string, string, IEnumerable<string>>? directories = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        exists ??= File.Exists;
        directories ??= (folder, pattern) =>
            Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, pattern) : Enumerable.Empty<string>();

        var chosen = environment("PEEL_BACKEND_FFPROBE");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            var expanded = Environment.ExpandEnvironmentVariables(chosen.Trim());
            if (exists(expanded))
            {
                return expanded;
            }
        }
        foreach (var folder in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Candidate(folder.Trim().Trim('"'), exists);
            if (candidate is not null)
            {
                return candidate;
            }
        }
        var programFiles = environment("ProgramFiles") ?? @"C:\Program Files";
        var local = environment("LOCALAPPDATA") ?? string.Empty;
        var fixedFolders = new List<string> { Path.Combine(programFiles, "ffmpeg", "bin"), @"C:\ffmpeg\bin" };
        if (local.Length > 0)
        {
            fixedFolders.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
        }
        foreach (var folder in fixedFolders)
        {
            var candidate = Candidate(folder, exists);
            if (candidate is not null)
            {
                return candidate;
            }
        }
        if (local.Length > 0)
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            foreach (var package in directories(packages, "Gyan.FFmpeg*"))
            {
                foreach (var build in directories(package, "*"))
                {
                    var candidate = Candidate(Path.Combine(build, "bin"), exists);
                    if (candidate is not null)
                    {
                        return candidate;
                    }
                }
            }
        }
        return null;
    }

    private static string? Candidate(string folder, Func<string, bool> exists)
    {
        if (folder.Length == 0)
        {
            return null;
        }
        try
        {
            var path = Path.Combine(folder, "ffprobe.exe");
            return exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry is not a reason to stop looking.
            return null;
        }
    }

    /// <summary>Probe one file. Never throws: a failure is (null, the reason).</summary>
    internal static Task<(MediaInfo? Info, string Problem)> ReadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => ReadOnPoolAsync(path, token), CancellationToken.None);

    private static async Task<(MediaInfo? Info, string Problem)> ReadOnPoolAsync(string path, CancellationToken token)
    {
        var name = Path.GetFileName(path);
        var ffprobe = Locate();
        if (ffprobe is null)
        {
            return (null, $"Peel needs FFprobe to read {name}. Install it with: winget install Gyan.FFmpeg");
        }
        using var job = ProcessJob.Create();
        if (job is null)
        {
            return (null, $"Peel could not set up the Windows job for FFprobe, so it did not read {name}.");
        }
        // An absolute path, so a name that starts with a dash is never an option (av._arg).
        var arguments = new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", Path.GetFullPath(path) };
        ContainedProcess process;
        try
        {
            process = ContainedProcess.Start(ffprobe, arguments, new Dictionary<string, string>(), job);
        }
        catch (Exception error)
        {
            Log.Write($"cannot start ffprobe at {ffprobe}", error);
            return (null, $"Peel could not start FFprobe to read {name}: {error.Message}");
        }
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(Deadline);
            var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync(limit.Token);
            var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                await Task.WhenAll(output, errors).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.Terminate();
                process.Kill();
                process.ClosePipes();
                try
                {
                    await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cancelled or broken: both expected after a kill.
                }
                _ = output.Exception;
                _ = errors.Exception;
                return (null, $"FFprobe took too long to read {name}.");
            }
            if (process.ExitCode != 0)
            {
                var said = (await errors.ConfigureAwait(false)).Trim().Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
                Log.Write($"ffprobe refused {path}: exit {process.ExitCode} {said}");
                return (null, $"{name} is not a media file FFmpeg can read{(said.Length > 0 ? ": " + said : string.Empty)}");
            }
            var info = MediaInfo.Parse(await output.ConfigureAwait(false));
            Log.Write(string.Format(CultureInfo.InvariantCulture,
                "probe {0}: {1:0.000}s video={2} audio={3}", name, info.Duration, info.HasVideo, info.HasAudio));
            return (info, string.Empty);
        }
    }
}
