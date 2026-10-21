// What the user can change, as one immutable value. The settings window edits
// a copy and saves it whole; everything else reads SettingsStore.Current at
// the moment it needs a value, so a save takes effect on the next drag, the
// next key press or the next job with no restart.
//
// The shell still knows no formats. Remapping a wheel is a permutation of the
// three modifier keys over the engine's own default_modifier names, and a
// quality preset only fills in a parameter a Compress entry already declares.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Peel.Engine;

namespace Peel.Settings;

internal sealed record PeelSettings
{
    /// <summary>The three keys a wheel can be bound to, in the engine's spelling.</summary>
    internal static readonly IReadOnlyList<string> ModifierKeys = new[] { "Shift", "Ctrl", "Alt" };

    internal const string OutputNextToSource = "source";
    internal const string OutputFolderMode = "folder";

    /// <summary>
    /// Which key opens each wheel, keyed by the wheel's default modifier as the
    /// engine names it: { "Shift": "Ctrl" } means the wheel the engine puts on
    /// Shift now opens with Ctrl. Always a permutation of the three keys.
    /// </summary>
    [JsonPropertyName("modifiers")]
    public Dictionary<string, string> Modifiers { get; init; } = DefaultModifiers();

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; init; } = HotkeyChord.Default.ToString();

    /// <summary>"source" writes next to the file; "folder" writes to OutputFolder.</summary>
    [JsonPropertyName("output")]
    public string Output { get; init; } = OutputNextToSource;

    [JsonPropertyName("outputFolder")]
    public string OutputFolder { get; init; } = string.Empty;

    /// <summary>One of QualityPresets.Names.</summary>
    [JsonPropertyName("quality")]
    public string Quality { get; init; } = QualityPresets.Balanced;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; init; } = true;

    internal static Dictionary<string, string> DefaultModifiers() =>
        ModifierKeys.ToDictionary(key => key, key => key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The engine modifier name a physical key stands for under this mapping.
    /// The wheel code looks families up by the engine's names, so translating
    /// here is the whole remap.
    /// </summary>
    internal string WheelKeyFor(string pressed)
    {
        foreach (var pair in Modifiers)
        {
            if (string.Equals(pair.Value, pressed, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }
        return pressed;
    }

    /// <summary>The physical key that opens the wheel the engine puts on <paramref name="engineKey"/>.</summary>
    internal string KeyForWheel(string engineKey) =>
        Modifiers.TryGetValue(engineKey, out var key) ? key : engineKey;

    /// <summary>
    /// The folder to pass as --out-dir, or null for next to the source. A
    /// folder that is not there (an unplugged drive) falls back to next to
    /// the source rather than failing every job.
    /// </summary>
    internal string? OutputDirectory()
    {
        if (!string.Equals(Output, OutputFolderMode, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(OutputFolder))
        {
            return null;
        }
        return Directory.Exists(OutputFolder) ? OutputFolder : null;
    }

    /// <summary>
    /// Why these settings cannot be saved, or null when they can. The window
    /// shows the message as written.
    /// </summary>
    internal string? Problem()
    {
        var keys = ModifierKeys.Select(KeyForWheel).ToList();
        if (keys.Any(key => !ModifierKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ModifierKeys.Count)
        {
            return "Each wheel needs its own key. Two wheels cannot share Shift, Ctrl or Alt.";
        }
        if (HotkeyChord.Parse(Hotkey) is null)
        {
            return "The hotkey needs Ctrl, Alt or Win plus one other key.";
        }
        if (string.Equals(Output, OutputFolderMode, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(OutputFolder) || !Path.IsPathFullyQualified(OutputFolder))
            {
                return "Choose a folder for the output, or write next to the source.";
            }
        }
        if (!QualityPresets.Names.Contains(Quality))
        {
            return "Choose a quality preset.";
        }
        return null;
    }

    /// <summary>
    /// A loaded file can hold anything. Keep what is valid, put back the
    /// default for what is not, so a hand edit can never stop Peel starting.
    /// </summary>
    internal PeelSettings Normalized()
    {
        var modifiers = DefaultModifiers();
        foreach (var pair in Modifiers ?? new Dictionary<string, string>())
        {
            var wheel = ModifierKeys.FirstOrDefault(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase));
            var pressed = ModifierKeys.FirstOrDefault(key => string.Equals(key, pair.Value, StringComparison.OrdinalIgnoreCase));
            if (wheel is not null)
            {
                // An unknown key name is kept so the check below throws the
                // whole mapping out rather than half of it.
                modifiers[wheel] = pressed ?? pair.Value ?? string.Empty;
            }
        }
        var candidate = this with { Modifiers = modifiers };
        var keys = ModifierKeys.Select(candidate.KeyForWheel).ToList();
        if (keys.Any(key => !ModifierKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ModifierKeys.Count)
        {
            candidate = candidate with { Modifiers = DefaultModifiers() };
        }
        if (HotkeyChord.Parse(candidate.Hotkey) is not { } chord)
        {
            candidate = candidate with { Hotkey = HotkeyChord.Default.ToString() };
        }
        else
        {
            candidate = candidate with { Hotkey = chord.ToString() };
        }
        var output = string.Equals(candidate.Output, OutputFolderMode, StringComparison.OrdinalIgnoreCase)
            ? OutputFolderMode
            : OutputNextToSource;
        candidate = candidate with { Output = output, OutputFolder = candidate.OutputFolder ?? string.Empty };
        if (!QualityPresets.Names.Contains(candidate.Quality))
        {
            candidate = candidate with { Quality = QualityPresets.Balanced };
        }
        return candidate;
    }
}

/// <summary>
/// The quality presets. Each fills in a parameter by name, and only for a
/// Compress entry whose registry params declare that name with a type, range
/// or choice list that accepts the value, so a preset can never send an engine
/// action a parameter it does not know or a value it would refuse. Other tools
/// that happen to declare a quality (video Trim, Crop, Join) keep the engine's
/// default: the setting is called Compress quality.
/// </summary>
internal static class QualityPresets
{
    internal const string Smaller = "smaller";
    internal const string Balanced = "balanced";
    internal const string Better = "better";

    internal static readonly IReadOnlyList<string> Names = new[] { Smaller, Balanced, Better };

    internal static string Label(string name) => name switch
    {
        Smaller => "Smaller files",
        Better => "Better quality",
        _ => "Balanced",
    };

    // Per parameter name, the candidate values in order: the first one the
    // entry's own spec accepts is sent. Image quality is an int from 1 to 100,
    // video quality a choice of low, medium, high, audio bitrate an int in
    // kbps. Balanced is the engine's own default for each, so it changes nothing.
    private static readonly Dictionary<string, Dictionary<string, string[]>> Values = new()
    {
        [Smaller] = new() { ["quality"] = new[] { "60", "low" }, ["bitrate"] = new[] { "96" } },
        [Balanced] = new() { ["quality"] = new[] { "80", "medium" }, ["bitrate"] = new[] { "128" } },
        [Better] = new() { ["quality"] = new[] { "92", "high" }, ["bitrate"] = new[] { "192" } },
    };

    /// <summary>
    /// The parameters to run with: what the caller chose, plus the preset's
    /// value for each parameter the entry declares, the caller left unset, and
    /// the spec accepts.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Apply(
        ActionEntry entry, IReadOnlyDictionary<string, string> chosen, string preset)
    {
        if (!IsCompress(entry) || !Values.TryGetValue(preset, out var values))
        {
            return chosen;
        }
        Dictionary<string, string>? merged = null;
        foreach (var spec in entry.Params)
        {
            if (chosen.ContainsKey(spec.Name) || !values.TryGetValue(spec.Name, out var candidates))
            {
                continue;
            }
            var value = candidates.FirstOrDefault(candidate => Accepts(spec, candidate));
            if (value is not null)
            {
                merged ??= new Dictionary<string, string>(chosen);
                merged[spec.Name] = value;
            }
        }
        return merged ?? chosen;
    }

    /// <summary>
    /// True for a Compress tool: the action part of the registry id (after the
    /// family's dot) is "compress" or ends in "_compress", as in image.compress,
    /// av.video_compress and av.audio_compress. The id is the stable key; the
    /// label is display text.
    /// </summary>
    internal static bool IsCompress(ActionEntry entry)
    {
        var action = entry.Id[(entry.Id.LastIndexOf('.') + 1)..];
        return action == "compress" || action.EndsWith("_compress", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the engine would take <paramref name="value"/> for this
    /// param: one of its choices when it lists any, else a whole number in
    /// range for an int. Any other type is left to the engine's default.
    /// </summary>
    internal static bool Accepts(ParamSpec spec, string value)
    {
        if (spec.Choices.Count > 0)
        {
            return spec.Choices.Any(choice => choice.ValueKind == System.Text.Json.JsonValueKind.String
                ? choice.GetString() == value
                : choice.GetRawText() == value);
        }
        if (spec.Type != "int"
            || !long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }
        return (spec.Minimum is not { } min || number >= min)
            && (spec.Maximum is not { } max || number <= max);
    }
}
