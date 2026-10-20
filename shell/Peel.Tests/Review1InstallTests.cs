// Review round 1 for hq-plct.12 (settings and install). The entry JSON below is
// copied verbatim from `peel options` (engine on this branch): the video tools
// declare "quality" as an enum of high, medium, low, not the 1 to 100 integer
// the image tool uses. A preset that sends "80" to them makes the engine
// refuse the job: `peel run av.video_compress clip.mp4 --param quality=80`
// returns {"ok": false, "message": "Quality (quality): expected one of high,
// medium, low, got '80'", "code": "invalid_params"}.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using Peel.Engine;
using Peel.Settings;
using Xunit;

namespace Peel.Tests;

public class Review1InstallTests
{
    private const string VideoCompress = """
        {"id": "av.video_compress", "family": "tool", "label": "Compress", "group": "Video",
         "inputs": ["mp4"], "output_ext": null, "mode": "each",
         "params": [{"name": "quality", "type": "enum", "label": "Quality", "default": "medium",
                     "required": false, "choices": ["high", "medium", "low"],
                     "help": "Ignored when a target size is given."}]}
        """;

    public static IEnumerable<object[]> Presets() => QualityPresets.Names.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Presets))]
    public void APresetNeverSendsAValueTheEntryDoesNotAccept(string preset)
    {
        var entry = JsonSerializer.Deserialize<ActionEntry>(VideoCompress)!;
        var spec = entry.Params.Single(p => p.Name == "quality");
        var allowed = spec.Choices.Select(c => c.ToString()).ToList();

        // A plain drop on Compress: no editor, so the wheel passes no params.
        var sent = QualityPresets.Apply(entry, new Dictionary<string, string>(), preset);

        if (sent.TryGetValue("quality", out var value))
        {
            Assert.Contains(value, allowed);
        }
    }

    [Theory]
    [InlineData("""{"modifiers": null, "hotkey": null, "output": null, "outputFolder": null, "quality": null}""")]
    [InlineData("""{"modifiers": {"Shift": "Ctrl", "Ctrl": "Ctrl", "Alt": "Alt"}}""")]
    [InlineData("""{"modifiers": {"Shift": null}}""")]
    [InlineData("""{"startWithWindows": "yes"}""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{"hotkey": "Shift+A", """)]
    public void ABrokenSettingsFileStillLoadsUsableSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"peel-review1-{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var settings = SettingsStore.Read(path, out _);
            Assert.Null(settings.Problem());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Round 1 fixes: a preset maps per type. An int quality takes 60, 80, 92;
    // a choice quality takes low, medium, high; bitrate stays in kbps.
    [Theory]
    [InlineData("smaller", "60", "low", "96")]
    [InlineData("balanced", "80", "medium", "128")]
    [InlineData("better", "92", "high", "192")]
    public void APresetMapsToEachParamsOwnType(string preset, string image, string video, string audio)
    {
        var none = new Dictionary<string, string>();
        var imageEntry = new ActionEntry { Id = "image.compress" };
        imageEntry.Params.Add(new ParamSpec { Name = "quality", Type = "int", Minimum = 1, Maximum = 100 });
        var audioEntry = new ActionEntry { Id = "av.audio_compress" };
        audioEntry.Params.Add(new ParamSpec { Name = "bitrate", Type = "int", Minimum = 32, Maximum = 320 });

        Assert.Equal(image, QualityPresets.Apply(imageEntry, none, preset)["quality"]);
        Assert.Equal(video, QualityPresets.Apply(JsonSerializer.Deserialize<ActionEntry>(VideoCompress)!, none, preset)["quality"]);
        Assert.Equal(audio, QualityPresets.Apply(audioEntry, none, preset)["bitrate"]);
    }

    [Theory]
    [InlineData("int", 1, 50)]
    [InlineData("float", null, null)]
    [InlineData("string", null, null)]
    public void APresetLeavesAParamItsValueDoesNotFit(string type, int? min, int? max)
    {
        var entry = new ActionEntry { Id = "x.other" };
        entry.Params.Add(new ParamSpec { Name = "quality", Type = type, Minimum = min, Maximum = max });
        var none = new Dictionary<string, string>();
        Assert.Same(none, QualityPresets.Apply(entry, none, QualityPresets.Better));
    }

    // A failed settings save puts the Run value back (lucy 2), and turning
    // start with Windows on names the installed exe, never a missing one or
    // the running dev build (coordinator note). Each test owns a scratch key.
    private static string ScratchRunKey() => $@"Software\PeelTests\{System.Guid.NewGuid():N}\Run";

    private static void DropScratch(string runKey) =>
        Registry.CurrentUser.DeleteSubKeyTree(runKey[..runKey.LastIndexOf('\\')], throwOnMissingSubKey: false);

    [Theory]
    [InlineData("\"C:\\old\\Peel.exe\"", true)]
    [InlineData("\"C:\\old\\Peel.exe\"", false)]
    [InlineData(null, true)]
    public void AFailedSaveLeavesTheRunValueAsItWas(string? previous, bool startWithWindows)
    {
        var runKey = ScratchRunKey();
        var exe = Path.GetTempFileName();
        var folder = Directory.CreateTempSubdirectory("peel-review1-").FullName;
        try
        {
            if (previous is not null)
            {
                using var key = Registry.CurrentUser.CreateSubKey(runKey);
                key.SetValue(StartupEntry.ValueName, previous);
            }
            // The settings path is a folder, so the final move fails.
            var problem = SettingsWindow.SaveWithStartup(
                new PeelSettings { StartWithWindows = startWithWindows }, exe, runKey, folder);

            Assert.NotNull(problem);
            Assert.Equal(previous, StartupEntry.Read(runKey));
        }
        finally
        {
            DropScratch(runKey);
            File.Delete(exe);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AGoodSaveWritesTheRunValueForTheInstalledExe()
    {
        var runKey = ScratchRunKey();
        var exe = Path.GetTempFileName();
        var settings = Path.Combine(Path.GetTempPath(), $"peel-review1-{System.Guid.NewGuid():N}.json");
        var before = SettingsStore.Current;
        try
        {
            Assert.Null(SettingsWindow.SaveWithStartup(new PeelSettings(), exe, runKey, settings));
            Assert.Equal(StartupEntry.CommandFor(exe), StartupEntry.Read(runKey));
        }
        finally
        {
            SettingsStore.Use(before);
            DropScratch(runKey);
            File.Delete(exe);
            File.Delete(settings);
        }
    }

    [Fact]
    public void StartWithWindowsNeverNamesAMissingExe()
    {
        var runKey = ScratchRunKey();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(runKey))
            {
                key.SetValue(StartupEntry.ValueName, "\"C:\\installed\\Peel.exe\"");
            }
            var missing = Path.Combine(Path.GetTempPath(), $"peel-review1-{System.Guid.NewGuid():N}", "Peel.exe");
            Assert.Null(StartupEntry.Set(true, missing, runKey));
            Assert.Equal("\"C:\\installed\\Peel.exe\"", StartupEntry.Read(runKey));
            Assert.EndsWith(@"Peel\app\Peel.exe", StartupEntry.InstalledExe);
        }
        finally
        {
            DropScratch(runKey);
        }
    }
}
