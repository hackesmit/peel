// What goes on the wheel: segments built from whatever `peel options` returned.
//
// No format, tool or action is named anywhere in this file. The shell asks the
// engine which entries are valid for the dragged files and draws them; a new
// family in the registry appears on the wheel with no change here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peel.Engine;

namespace Peel.Wheel;

internal sealed record DraggedFile(string Path, string Extension, bool IsDirectory)
{
    /// <summary>
    /// Extensions are compared the way the engine normalises them: no dot,
    /// lower case. A folder has no extension.
    /// </summary>
    internal static DraggedFile FromPath(string path)
    {
        var isDirectory = false;
        try
        {
            isDirectory = Directory.Exists(path);
        }
        catch (Exception)
        {
            // An unreadable path is treated as a file; `peel run` reports it.
        }
        var extension = isDirectory ? string.Empty : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return new DraggedFile(path, extension, isDirectory);
    }
}

internal enum SegmentKind
{
    /// <summary>A real registry entry. Dropping here runs it.</summary>
    Entry,

    /// <summary>Turns the wheel to the next page of entries.</summary>
    More,

    /// <summary>Nothing to offer. Dropping here does nothing.</summary>
    Empty,
}

internal sealed record WheelSegment(SegmentKind Kind, string Label, string Detail, ActionEntry? Entry)
{
    internal bool Runnable => Kind == SegmentKind.Entry && Entry is not null;
}

internal sealed record WheelPlan(
    string Family,
    string Label,
    IReadOnlyList<WheelSegment> Segments,
    int Page,
    int PageCount)
{
    internal static readonly WheelPlan Loading = new(
        string.Empty,
        "Reading the drag",
        new[] { new WheelSegment(SegmentKind.Empty, "...", string.Empty, null) },
        0,
        1);

    internal int Count => Segments.Count;
}

internal static class WheelPlanner
{
    /// <summary>
    /// Segments past this count are too thin to aim at mid drag, so the wheel
    /// pages instead.
    /// </summary>
    internal const int MaxSegments = 12;

    /// <summary>Every entry of one wheel, in the order the engine grouped them.</summary>
    internal static IReadOnlyList<ActionEntry> Flatten(WheelSpec? wheel) =>
        wheel is null
            ? Array.Empty<ActionEntry>()
            : wheel.Groups.SelectMany(group => group.Entries).ToList();

    /// <summary>
    /// The same test the engine's Action.why_not applies, so the wheel can be
    /// drawn from the preloaded registry before `peel options` answers for
    /// this particular drag. The engine stays the authority: it checks again
    /// in `peel run` and its refusal is what the user is shown.
    /// </summary>
    internal static bool Accepts(ActionEntry entry, IReadOnlyList<DraggedFile> files)
    {
        if (files.Count < entry.MinFiles)
        {
            return false;
        }
        if (entry.MaxFiles is { } most && files.Count > most)
        {
            return false;
        }
        foreach (var file in files)
        {
            if (file.IsDirectory)
            {
                if (!entry.AcceptsDirs)
                {
                    return false;
                }
                continue;
            }
            if (entry.AcceptsAny)
            {
                continue;
            }
            if (!entry.Inputs.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    internal static IReadOnlyList<ActionEntry> Filter(
        IEnumerable<ActionEntry> entries,
        IReadOnlyList<DraggedFile> files) =>
        entries.Where(entry => entry.Available && Accepts(entry, files)).ToList();

    /// <summary>Build one page of a wheel. Page numbers wrap.</summary>
    internal static WheelPlan Build(
        string family,
        string label,
        IReadOnlyList<ActionEntry> entries,
        int page = 0,
        int maxSegments = MaxSegments)
    {
        if (maxSegments < 2)
        {
            maxSegments = 2;
        }
        if (entries.Count == 0)
        {
            return new WheelPlan(
                family,
                label,
                new[] { new WheelSegment(SegmentKind.Empty, "Nothing here", "No entry fits these files", null) },
                0,
                1);
        }
        if (entries.Count <= maxSegments)
        {
            return new WheelPlan(family, label, entries.Select(ToSegment).ToList(), 0, 1);
        }

        var perPage = maxSegments - 1;
        var pageCount = (int)Math.Ceiling(entries.Count / (double)perPage);
        var index = ((page % pageCount) + pageCount) % pageCount;
        var segments = entries
            .Skip(index * perPage)
            .Take(perPage)
            .Select(ToSegment)
            .ToList();
        segments.Add(new WheelSegment(
            SegmentKind.More,
            "More",
            $"Page {index + 1} of {pageCount}",
            null));
        return new WheelPlan(family, label, segments, index, pageCount);
    }

    private static WheelSegment ToSegment(ActionEntry entry) =>
        new(SegmentKind.Entry, entry.Label, entry.Group, entry);
}

/// <summary>
/// The wheels one `peel options` call returned, keyed by family, plus the
/// modifier each one opens with. The mapping comes from the engine's
/// default_modifier field, so the shell never decides which key means what.
/// </summary>
internal sealed class WheelCatalog
{
    private readonly Dictionary<string, WheelSpec> _byFamily = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _modifiers = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> Families { get; private set; } = Array.Empty<string>();

    internal static WheelCatalog From(OptionsResult? options)
    {
        var catalog = new WheelCatalog();
        if (options is null)
        {
            return catalog;
        }
        foreach (var wheel in options.Wheels)
        {
            catalog._byFamily[wheel.Family] = wheel;
            if (!string.IsNullOrEmpty(wheel.DefaultModifier))
            {
                catalog._modifiers[wheel.DefaultModifier] = wheel.Family;
            }
        }
        catalog.Families = options.Wheels.Select(wheel => wheel.Family).ToList();
        return catalog;
    }

    internal bool IsEmpty => _byFamily.Count == 0;

    internal WheelSpec? Wheel(string family) =>
        _byFamily.TryGetValue(family, out var wheel) ? wheel : null;

    internal string? FamilyForModifier(string modifier) =>
        _modifiers.TryGetValue(modifier, out var family) ? family : null;

    internal string LabelFor(string family) => Wheel(family)?.Label ?? family;

    /// <summary>Merge in modifier mappings learned from another catalog.</summary>
    internal WheelCatalog WithModifiersFrom(WheelCatalog other)
    {
        foreach (var pair in other._modifiers)
        {
            _modifiers.TryAdd(pair.Key, pair.Value);
        }
        return this;
    }
}
