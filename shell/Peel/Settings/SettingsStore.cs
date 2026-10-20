// %APPDATA%\Peel\settings.json, read once at start up and written whole by the
// settings window. PEEL_SETTINGS points it somewhere else, so a test run never
// reads or writes the user's own file.
//
// A missing file is the defaults and is not written until the user saves. A
// file that does not parse is logged and left alone (the user's hand edit is
// not destroyed); Peel runs on the defaults until the next save.
using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Peel.Settings;

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // The file is for a person to read too: "Ctrl+Alt+Enter" and a folder
        // with accents stay as written instead of \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static volatile PeelSettings _current = new();

    /// <summary>The settings in force. Safe to read from any thread.</summary>
    internal static PeelSettings Current => _current;

    /// <summary>Raised on the saving thread after a save changed Current.</summary>
    internal static event Action<PeelSettings, PeelSettings>? Changed;

    internal static string Path
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("PEEL_SETTINGS");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(roaming, "Peel", "settings.json");
        }
    }

    /// <summary>Read the file into Current. Never throws.</summary>
    internal static PeelSettings Load()
    {
        _current = Read(Path, out var note);
        Log.Write($"settings: {note}");
        return _current;
    }

    internal static PeelSettings Read(string path, out string note)
    {
        try
        {
            if (!File.Exists(path))
            {
                note = $"no file at {path}, using the defaults";
                return new PeelSettings();
            }
            var loaded = JsonSerializer.Deserialize<PeelSettings>(File.ReadAllText(path, Encoding.UTF8), Json);
            if (loaded is null)
            {
                note = $"{path} is empty, using the defaults";
                return new PeelSettings();
            }
            var normal = loaded.Normalized();
            note = $"loaded {path}: {Describe(normal)}";
            return normal;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            note = $"cannot read {path} ({error.Message}), using the defaults";
            return new PeelSettings();
        }
    }

    /// <summary>
    /// Write the settings and make them Current. Returns null on success, else
    /// the message the window shows. The file is replaced in one move, so a
    /// crash mid write leaves the old one.
    /// </summary>
    internal static string? Save(PeelSettings settings, string? path = null)
    {
        var problem = settings.Problem();
        if (problem is not null)
        {
            return problem;
        }
        var normal = settings.Normalized();
        path ??= Path;
        try
        {
            Write(path, normal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"cannot save the settings to {path}", error);
            return $"Peel could not save the settings: {error.Message}";
        }
        var before = _current;
        _current = normal;
        Log.Write($"settings saved to {path}: {Describe(normal)}");
        try
        {
            Changed?.Invoke(before, normal);
        }
        catch (Exception error)
        {
            Log.Write("a settings listener threw", error);
        }
        return null;
    }

    internal static void Write(string path, PeelSettings settings)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>For tests: put a value in force without touching a file.</summary>
    internal static void Use(PeelSettings settings) => _current = settings.Normalized();

    internal static string Describe(PeelSettings s) =>
        $"wheels Shift>{s.KeyForWheel("Shift")} Ctrl>{s.KeyForWheel("Ctrl")} Alt>{s.KeyForWheel("Alt")}, "
        + $"hotkey {s.Hotkey}, output {(s.Output == PeelSettings.OutputFolderMode ? s.OutputFolder : "next to source")}, "
        + $"quality {s.Quality}, start with Windows {(s.StartWithWindows ? "on" : "off")}";
}
