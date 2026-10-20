// The shapes `peel options` and `peel run` return. The shell knows these five
// classes and nothing else about what Peel can do: every format, tool and
// action on the wheel comes from the registry at run time, so a new family in
// the engine shows up here with no change to the shell.
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Peel.Engine;

internal sealed class ParamSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "string";
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("default")] public JsonElement Default { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("choices")] public List<JsonElement> Choices { get; set; } = new();
    // The engine declares the range of a number here. The editors read it so a
    // slider or a numeric field never repeats a limit the registry already
    // states; null means the engine set no limit on that side.
    [JsonPropertyName("minimum")] public double? Minimum { get; set; }
    [JsonPropertyName("maximum")] public double? Maximum { get; set; }
    [JsonPropertyName("help")] public string Help { get; set; } = string.Empty;
}

internal sealed class ActionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("family")] public string Family { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("group")] public string Group { get; set; } = string.Empty;
    [JsonPropertyName("inputs")] public List<string> Inputs { get; set; } = new();
    [JsonPropertyName("output_ext")] public string? OutputExt { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "each";
    [JsonPropertyName("needs_editor")] public bool NeedsEditor { get; set; }
    [JsonPropertyName("min_files")] public int MinFiles { get; set; } = 1;
    [JsonPropertyName("max_files")] public int? MaxFiles { get; set; }
    [JsonPropertyName("accepts_dirs")] public bool AcceptsDirs { get; set; }
    [JsonPropertyName("handled_by")] public string HandledBy { get; set; } = "engine";
    [JsonPropertyName("params")] public List<ParamSpec> Params { get; set; } = new();
    [JsonPropertyName("missing_backends")] public List<string> MissingBackends { get; set; } = new();
    [JsonPropertyName("available")] public bool Available { get; set; } = true;

    /// <summary>True when the entry accepts any extension (the Alt wheel actions).</summary>
    internal bool AcceptsAny => Inputs.Contains("*");

    /// <summary>The tray app performs this one itself instead of calling `peel run`.</summary>
    internal bool IsShellAction => HandledBy == "shell";
}

internal sealed class WheelGroup
{
    [JsonPropertyName("group")] public string Group { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public List<ActionEntry> Entries { get; set; } = new();
}

internal sealed class WheelSpec
{
    [JsonPropertyName("family")] public string Family { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("default_modifier")] public string DefaultModifier { get; set; } = string.Empty;
    [JsonPropertyName("groups")] public List<WheelGroup> Groups { get; set; } = new();
}

internal sealed class OptionsResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("wheels")] public List<WheelSpec> Wheels { get; set; } = new();
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string? Code { get; set; }

    internal static OptionsResult Failed(string message, string code) =>
        new() { Ok = false, Message = message, Code = code };
}

internal sealed class RunResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("outputs")] public List<string> Outputs { get; set; } = new();
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("data")] public JsonElement Data { get; set; }

    internal static RunResult Failed(string message, string code) =>
        new() { Ok = false, Message = message, Code = code };
}
