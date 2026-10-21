// The pages of a PDF as the user has arranged them, and what that means to the engine.
//
// Organize (pdf.organize) keeps a list of the source's pages in a new order,
// each with a quarter turn count, and sends "pages" (1-based source numbers in
// the new order, the ones left out are deleted) and "rotate" (clockwise
// degrees added to a page's own rotation, keyed by source page). Split
// (pdf.split) keeps cuts between pages in the source order and sends "ranges"
// ("1-3,4,5-6"), or nothing at all for one file per page, which is the
// engine's own default.
//
// Every edit is undoable, and a plan the user has not changed refuses to
// apply: organizing a PDF into the same PDF is a copy, not a tool.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Peel.Editors.Pages;

internal sealed record PageItem(int Source, int Turn);

internal sealed class PagePlan
{
    private readonly List<PageItem> _pages;
    private readonly Stack<List<PageItem>> _undo = new();
    private readonly SortedSet<int> _cutsAfter = new();

    internal PagePlan(int count)
    {
        Count = Math.Max(0, count);
        _pages = Enumerable.Range(1, Count).Select(source => new PageItem(source, 0)).ToList();
    }

    /// <summary>How many pages the source has.</summary>
    internal int Count { get; }

    internal IReadOnlyList<PageItem> Pages => _pages;

    /// <summary>Split: the source pages a cut follows (a cut after 3 starts a file at 4).</summary>
    internal IReadOnlyCollection<int> CutsAfter => _cutsAfter;

    internal bool Changed =>
        _pages.Count != Count || _pages.Select((page, index) => page.Source != index + 1 || page.Turn != 0).Any(moved => moved);

    internal bool CanUndo => _undo.Count > 0;

    // --- organize -----------------------------------------------------------

    /// <summary>
    /// Move the page at <paramref name="from"/> so that it lands before the
    /// page that is at <paramref name="before"/> now (Count means last), which
    /// is what a drop between two thumbnails means.
    /// </summary>
    internal bool MoveBefore(int from, int before)
    {
        if (from < 0 || from >= _pages.Count || before < 0 || before > _pages.Count)
        {
            return false;
        }
        var to = before > from ? before - 1 : before;
        if (to == from)
        {
            return false;
        }
        Remember();
        var page = _pages[from];
        _pages.RemoveAt(from);
        _pages.Insert(to, page);
        return true;
    }

    /// <summary>A quarter turn at a time: 90 is clockwise, -90 anticlockwise.</summary>
    internal bool Turn(int index, int degrees)
    {
        if (index < 0 || index >= _pages.Count || degrees % 90 != 0)
        {
            return false;
        }
        Remember();
        var page = _pages[index];
        _pages[index] = page with { Turn = ((page.Turn + degrees) % 360 + 360) % 360 };
        return true;
    }

    /// <summary>Delete one page. The last page left cannot go: a PDF with no pages is not a PDF.</summary>
    internal bool Remove(int index)
    {
        if (index < 0 || index >= _pages.Count || _pages.Count == 1)
        {
            return false;
        }
        Remember();
        _pages.RemoveAt(index);
        return true;
    }

    internal void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }
        var was = _undo.Pop();
        _pages.Clear();
        _pages.AddRange(was);
    }

    internal void Reset()
    {
        if (Changed)
        {
            Remember();
        }
        _pages.Clear();
        _pages.AddRange(Enumerable.Range(1, Count).Select(source => new PageItem(source, 0)));
    }

    private void Remember() => _undo.Push(_pages.ToList());

    internal string? OrganizeProblem() => Changed
        ? null
        : "Nothing has changed yet: drag a page, turn one or delete one.";

    internal Dictionary<string, string> ToOrganizeParams()
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pages"] = string.Join(",", _pages.Select(page => page.Source.ToString(CultureInfo.InvariantCulture))),
        };
        var turned = _pages.Where(page => page.Turn != 0).ToList();
        if (turned.Count > 0)
        {
            var json = new StringBuilder("{");
            json.Append(string.Join(",", turned.Select(page => string.Format(
                CultureInfo.InvariantCulture, "\"{0}\":{1}", page.Source, page.Turn))));
            json.Append('}');
            sent["rotate"] = json.ToString();
        }
        return sent;
    }

    internal string DescribeOrganize()
    {
        var turned = _pages.Count(page => page.Turn != 0);
        var kept = _pages.Count == Count
            ? $"Keeping all {Count} pages"
            : $"Keeping {_pages.Count} of {Count} pages";
        var order = string.Join(", ", _pages.Select(page => page.Source.ToString(CultureInfo.InvariantCulture)));
        var turns = turned switch
        {
            0 => string.Empty,
            1 => ", 1 turned",
            _ => $", {turned} turned",
        };
        return $"{kept} in the order {order}{turns}";
    }

    // --- split --------------------------------------------------------------

    /// <summary>Put a cut after this source page, or take it away. No cut after the last page.</summary>
    internal bool ToggleCut(int afterPage)
    {
        if (afterPage < 1 || afterPage >= Count)
        {
            return false;
        }
        if (!_cutsAfter.Remove(afterPage))
        {
            _cutsAfter.Add(afterPage);
        }
        return true;
    }

    internal void ClearCuts() => _cutsAfter.Clear();

    /// <summary>The cuts as engine ranges. No cuts is the empty string: one file per page.</summary>
    internal string RangesFromCuts()
    {
        if (_cutsAfter.Count == 0)
        {
            return string.Empty;
        }
        var parts = new List<string>();
        var first = 1;
        foreach (var last in _cutsAfter.Append(Count))
        {
            parts.Add(first == last
                ? first.ToString(CultureInfo.InvariantCulture)
                : string.Format(CultureInfo.InvariantCulture, "{0}-{1}", first, last));
            first = last + 1;
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// Check typed ranges the way pdf_tools._parse_ranges will: "1-3,5",
    /// 1-based, inside the document, never backwards. Empty is fine: one file
    /// per page. Returns how many files it makes, or -1 with the reason.
    /// </summary>
    internal static int CheckRanges(string? text, int count, out string problem)
    {
        problem = string.Empty;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return count;
        }
        var files = 0;
        foreach (var chunk in trimmed.Split(','))
        {
            var part = chunk.Trim();
            if (part.Length == 0)
            {
                continue;
            }
            var halves = part.Split('-');
            if (halves.Length > 2)
            {
                problem = $"\"{part}\" is not a page or a range. Write 1-3 or 5.";
                return -1;
            }
            var numbers = new List<int>();
            foreach (var half in halves)
            {
                if (!int.TryParse(half.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var page))
                {
                    problem = $"\"{half.Trim()}\" is not a page number.";
                    return -1;
                }
                if (page < 1 || page > count)
                {
                    problem = $"Page {page} is out of range (1 to {count}).";
                    return -1;
                }
                numbers.Add(page);
            }
            if (numbers.Count == 2 && numbers[1] < numbers[0])
            {
                problem = $"\"{part}\" ends before it starts.";
                return -1;
            }
            files++;
        }
        if (files == 0)
        {
            problem = "Name at least one page.";
            return -1;
        }
        return files;
    }

    internal static string DescribeSplit(string ranges, int files) => ranges.Trim().Length == 0
        ? $"One file per page, {files} files"
        : files == 1
            ? $"1 file: {ranges.Trim()}"
            : $"{files} files: {ranges.Trim()}";
}
