// The settings as data: the wheel remap, the hotkey chord, the quality
// presets and the file round trip. The window only edits these values.
using System;
using System.Collections.Generic;
using System.IO;
using Peel.Engine;
using Peel.Settings;
using Xunit;

namespace Peel.Tests;

public class SettingsTests
{
    private static PeelSettings Swapped() => new()
    {
        // The conversion wheel (engine: Shift) moves to Ctrl, tools (engine:
        // Ctrl) move to Shift, actions stay on Alt.
        Modifiers = new Dictionary<string, string> { ["Shift"] = "Ctrl", ["Ctrl"] = "Shift", ["Alt"] = "Alt" },
    };

    [Fact]
    public void DefaultsMapEveryKeyToItself()
    {
        var settings = new PeelSettings();
        foreach (var key in PeelSettings.ModifierKeys)
        {
            Assert.Equal(key, settings.WheelKeyFor(key));
            Assert.Equal(key, settings.KeyForWheel(key));
        }
        Assert.Null(settings.Problem());
        Assert.Null(settings.OutputDirectory());
        Assert.Equal("Ctrl+Alt+Enter", settings.Hotkey);
    }

    [Fact]
    public void ASwappedPairTranslatesThePressedKeyToTheEngineName()
    {
        var settings = Swapped();
        Assert.Equal("Shift", settings.WheelKeyFor("Ctrl"));
        Assert.Equal("Ctrl", settings.WheelKeyFor("Shift"));
        Assert.Equal("Alt", settings.WheelKeyFor("Alt"));
        Assert.Null(settings.Problem());
    }

    [Fact]
    public void TwoWheelsOnOneKeyCannotBeSavedAndAreResetWhenLoaded()
    {
        var settings = new PeelSettings
        {
            Modifiers = new Dictionary<string, string> { ["Shift"] = "Ctrl", ["Ctrl"] = "Ctrl", ["Alt"] = "Alt" },
        };
        Assert.Contains("own key", settings.Problem());
        var normal = settings.Normalized();
        Assert.Equal("Shift", normal.KeyForWheel("Shift"));
        Assert.Equal("Ctrl", normal.KeyForWheel("Ctrl"));
    }

    [Fact]
    public void AnUnknownKeyNameResetsTheWholeMapping()
    {
        var settings = new PeelSettings
        {
            Modifiers = new Dictionary<string, string> { ["Shift"] = "CapsLock", ["Ctrl"] = "Shift", ["Alt"] = "Alt" },
        };
        Assert.NotNull(settings.Problem());
        Assert.Equal(PeelSettings.DefaultModifiers(), settings.Normalized().Modifiers);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Enter", "Ctrl+Alt+Enter")]
    [InlineData("ctrl + shift + p", "Ctrl+Shift+P")]
    [InlineData("Alt+Ctrl+F12", "Ctrl+Alt+F12")]
    [InlineData("Win+Space", "Win+Space")]
    [InlineData("Control+Alt+7", "Ctrl+Alt+7")]
    public void AChordRoundTripsInItsOwnSpelling(string text, string expected)
    {
        var chord = HotkeyChord.Parse(text);
        Assert.NotNull(chord);
        Assert.Equal(expected, chord!.Value.ToString());
        Assert.Equal(chord, HotkeyChord.Parse(chord.Value.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Enter")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Hyper+A")]
    [InlineData("Ctrl+Alt+Banana")]
    public void AChordWithoutCtrlAltOrWinOrWithoutANamedKeyIsRefused(string text)
    {
        Assert.Null(HotkeyChord.Parse(text));
    }

    [Fact]
    public void TheDefaultChordIsTheOneTheKeyboardPathAlwaysUsed()
    {
        var chord = HotkeyChord.Default;
        Assert.Equal(0x0Du, chord.Vk);
        Assert.Equal(0x0002u | 0x0001u | 0x4000u, chord.ModifierFlags);
    }

    [Fact]
    public void ABareModifierPressIsNotAChord()
    {
        Assert.Null(HotkeyChord.FromPress(true, false, false, false, 0x11));
        Assert.Equal("Ctrl+Shift+K", HotkeyChord.FromPress(true, false, true, false, 'K')!.Value.ToString());
    }

    private static ActionEntry Entry(params string[] names)
    {
        var entry = new ActionEntry { Id = "image.compress", Label = "Compress" };
        foreach (var name in names)
        {
            entry.Params.Add(new ParamSpec { Name = name, Type = "int" });
        }
        return entry;
    }

    [Fact]
    public void APresetFillsOnlyParametersTheEntryDeclares()
    {
        var none = new Dictionary<string, string>();
        var image = QualityPresets.Apply(Entry("quality"), none, QualityPresets.Smaller);
        Assert.Equal("60", image["quality"]);
        Assert.False(image.ContainsKey("bitrate"));

        var audio = QualityPresets.Apply(Entry("bitrate"), none, QualityPresets.Better);
        Assert.Equal("192", audio["bitrate"]);

        var other = QualityPresets.Apply(Entry("width"), none, QualityPresets.Better);
        Assert.Same(none, other);
    }

    [Fact]
    public void AValueTheCallerChoseBeatsThePreset()
    {
        var chosen = new Dictionary<string, string> { ["quality"] = "33" };
        var result = QualityPresets.Apply(Entry("quality"), chosen, QualityPresets.Better);
        Assert.Equal("33", result["quality"]);
    }

    [Fact]
    public void AnOutputFolderThatIsMissingFallsBackToNextToTheSource()
    {
        var gone = Path.Combine(Path.GetTempPath(), "peel-no-such-" + Guid.NewGuid().ToString("N"));
        var settings = new PeelSettings { Output = PeelSettings.OutputFolderMode, OutputFolder = gone };
        Assert.Null(settings.OutputDirectory());
        Directory.CreateDirectory(gone);
        try
        {
            Assert.Equal(gone, settings.OutputDirectory());
        }
        finally
        {
            Directory.Delete(gone);
        }
    }

    [Fact]
    public void AFolderModeWithARelativeOrEmptyFolderCannotBeSaved()
    {
        Assert.NotNull(new PeelSettings { Output = PeelSettings.OutputFolderMode }.Problem());
        Assert.NotNull(new PeelSettings { Output = PeelSettings.OutputFolderMode, OutputFolder = "out" }.Problem());
    }

    [Fact]
    public void TheFileRoundTrips()
    {
        var folder = Path.Combine(Path.GetTempPath(), "peel-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "nested", "settings.json");
        try
        {
            var saved = Swapped() with
            {
                Hotkey = "Ctrl+Shift+P",
                Quality = QualityPresets.Smaller,
                StartWithWindows = false,
                Output = PeelSettings.OutputFolderMode,
                OutputFolder = folder,
            };
            SettingsStore.Write(path, saved);
            var text = File.ReadAllText(path);
            Assert.Contains("\"hotkey\": \"Ctrl+Shift+P\"", text);
            Assert.False(File.Exists(path + ".tmp"));

            var loaded = SettingsStore.Read(path, out var note);
            Assert.StartsWith("loaded", note);
            Assert.Equal("Ctrl", loaded.KeyForWheel("Shift"));
            Assert.Equal("Ctrl+Shift+P", loaded.Hotkey);
            Assert.Equal(QualityPresets.Smaller, loaded.Quality);
            Assert.False(loaded.StartWithWindows);
            Assert.Equal(folder, loaded.OutputFolder);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void AMissingOrBrokenFileGivesTheDefaultsAndIsLeftAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), "peel-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var missing = SettingsStore.Read(path, out var note);
        Assert.Contains("no file", note);
        Assert.Equal("Ctrl+Alt+Enter", missing.Hotkey);
        Assert.False(File.Exists(path));

        File.WriteAllText(path, "{ \"hotkey\": ");
        try
        {
            var broken = SettingsStore.Read(path, out note);
            Assert.Contains("cannot read", note);
            Assert.Equal(new PeelSettings().Hotkey, broken.Hotkey);
            Assert.Equal("{ \"hotkey\": ", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHandEditedFileKeepsWhatIsValid()
    {
        var path = Path.Combine(Path.GetTempPath(), "peel-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
              // comments and odd casing are fine
              "Hotkey": "not a chord",
              "quality": "better",
              "modifiers": { "shift": "alt", "ctrl": "ctrl", "alt": "shift" },
              "output": "FOLDER",
            }
            """);
        try
        {
            var loaded = SettingsStore.Read(path, out _);
            Assert.Equal("Ctrl+Alt+Enter", loaded.Hotkey);
            Assert.Equal(QualityPresets.Better, loaded.Quality);
            Assert.Equal("Alt", loaded.KeyForWheel("Shift"));
            Assert.Equal("Shift", loaded.KeyForWheel("Alt"));
            Assert.Equal(PeelSettings.OutputFolderMode, loaded.Output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheModifierNamesMatchTheDragStateMachine()
    {
        Assert.Equal("Shift", Peel.Hooks.DragStateMachine.ModifierName(0x10));
        Assert.Equal("Ctrl", Peel.Hooks.DragStateMachine.ModifierName(0x11));
        Assert.Equal("Alt", Peel.Hooks.DragStateMachine.ModifierName(0x12));
        Assert.Equal(PeelSettings.ModifierKeys, new[] { "Shift", "Ctrl", "Alt" });
    }
}
