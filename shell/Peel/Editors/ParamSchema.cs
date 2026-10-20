// The engine's parameter schema, read rather than copied.
//
// Every editor needs the same facts about the action it is collecting values
// for: which choices an enum allows, where a slider starts and stops, what the
// default is. All of that already travels in `peel options` (ActionEntry.Params)
// and the repo convention is that the shell never hard codes it, so a range
// widened in the engine widens the slider with no change here. An editor that
// asks for a parameter the entry does not declare gets its fallback, which is
// what keeps an older shell usable against a newer engine.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Peel.Engine;

namespace Peel.Editors;

internal sealed class ParamSchema
{
    private readonly Dictionary<string, ParamSpec> _byName;

    internal ParamSchema(ActionEntry entry)
    {
        _byName = new Dictionary<string, ParamSpec>(StringComparer.Ordinal);
        foreach (var spec in entry.Params)
        {
            if (spec.Name.Length > 0)
            {
                _byName[spec.Name] = spec;
            }
        }
    }

    internal bool Declares(string name) => _byName.ContainsKey(name);

    internal ParamSpec? Spec(string name) => _byName.TryGetValue(name, out var spec) ? spec : null;

    /// <summary>The label the engine gave the parameter, or a fallback.</summary>
    internal string Label(string name, string fallback)
    {
        var label = Spec(name)?.Label;
        return string.IsNullOrWhiteSpace(label) ? fallback : label;
    }

    internal double Minimum(string name, double fallback) => Spec(name)?.Minimum ?? fallback;

    internal double Maximum(string name, double fallback) => Spec(name)?.Maximum ?? fallback;

    /// <summary>The declared default as a number, or the fallback.</summary>
    internal double Number(string name, double fallback) =>
        Spec(name)?.Default is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number)
            ? number
            : fallback;

    /// <summary>The declared default as text, or the fallback.</summary>
    internal string Text(string name, string fallback) =>
        Scalar(Spec(name)?.Default) ?? fallback;

    /// <summary>
    /// The choices an enum declares, in the engine's order, as the strings the
    /// CLI will be given. An undeclared parameter has no choices, and the
    /// caller is expected to fall back rather than to draw an empty list.
    /// </summary>
    internal IReadOnlyList<string> Choices(string name) =>
        Spec(name) is { } spec
            ? spec.Choices.Select(choice => Scalar(choice)).Where(value => value is not null).Select(value => value!).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// One JSON scalar as the CLI sees it. A number keeps an invariant form
    /// ("90", not "90,0" under a comma locale), which is what the engine parses.
    /// </summary>
    private static string? Scalar(JsonElement? raw) => raw?.ValueKind switch
    {
        JsonValueKind.String => raw.Value.GetString(),
        JsonValueKind.Number => Format(raw.Value),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string Format(JsonElement number)
    {
        if (number.TryGetInt64(out var whole))
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }
        return number.TryGetDouble(out var real)
            ? real.ToString("0.###############", CultureInfo.InvariantCulture)
            : number.GetRawText();
    }
}
