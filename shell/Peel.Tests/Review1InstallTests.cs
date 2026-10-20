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
}
