// hq-plct.20: the Compress quality preset fills in a value only for a Compress
// tool. The video tools Trim, Crop, Join, Split, Speed and Redact declare the
// same "quality" choice (entry JSON copied from `peel options` on this
// branch), and before this change the preset set it on them too.
using System.Collections.Generic;
using System.Text.Json;
using Peel.Engine;
using Peel.Settings;
using Xunit;

namespace Peel.Tests;

public class Review20PresetScopeTests
{
    private static ActionEntry Video(string id, string label) => JsonSerializer.Deserialize<ActionEntry>($$"""
        {"id": "{{id}}", "family": "tool", "label": "{{label}}", "group": "Video",
         "inputs": ["mp4"], "output_ext": null, "mode": "each",
         "params": [{"name": "quality", "type": "enum", "label": "Quality", "default": "medium",
                     "required": false, "choices": ["high", "medium", "low"]}]}
        """)!;

    [Theory]
    [InlineData("av.video_trim", "Trim")]
    [InlineData("av.video_crop", "Crop")]
    [InlineData("av.video_join", "Join")]
    [InlineData("av.video_split", "Split")]
    [InlineData("av.video_speed", "Speed")]
    [InlineData("av.video_redact", "Redact")]
    public void APresetLeavesToolsOtherThanCompressAlone(string id, string label)
    {
        var none = new Dictionary<string, string>();
        foreach (var preset in QualityPresets.Names)
        {
            Assert.Same(none, QualityPresets.Apply(Video(id, label), none, preset));
        }
    }

    [Theory]
    [InlineData("av.video_compress", true)]
    [InlineData("av.audio_compress", true)]
    [InlineData("image.compress", true)]
    [InlineData("pdf.compress", true)]
    [InlineData("av.video_trim", false)]
    [InlineData("image.decompress", false)]
    [InlineData("compress.trim", false)]
    [InlineData("compress", true)]
    public void OnlyACompressIdCountsAsCompress(string id, bool expected)
    {
        Assert.Equal(expected, QualityPresets.IsCompress(new ActionEntry { Id = id }));
    }

    [Fact]
    public void VideoCompressStillGetsThePreset()
    {
        var sent = QualityPresets.Apply(Video("av.video_compress", "Compress"), new Dictionary<string, string>(), QualityPresets.Smaller);
        Assert.Equal("low", sent["quality"]);
    }
}
