// Where a page dropped in the grid goes.
//
// The thumbnails wrap, so "between which two pages" is a question about rows
// first and columns second. Given each thumbnail's rectangle in the order the
// pages are in, a point is answered with the index to insert before (the
// page count means at the end): the row is the one whose band holds the point
// (or the nearest), and within it, the first thumbnail whose middle is right
// of the point. Pure, so it is tested without a window.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Peel.Editors.Pages;

internal static class PageGrid
{
    internal static int InsertionIndex(Point point, IReadOnlyList<Rect> items)
    {
        if (items.Count == 0)
        {
            return 0;
        }
        // Rows: thumbnails that share a top edge (within half a thumbnail).
        var rows = new List<List<int>>();
        for (var i = 0; i < items.Count; i++)
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(items[row[0]].Top - items[i].Top) > items[row[0]].Height / 2)
            {
                rows.Add(new List<int> { i });
            }
            else
            {
                row.Add(i);
            }
        }
        var chosen = rows
            .OrderBy(row => Distance(point.Y, items[row[0]].Top, row.Max(index => items[index].Bottom)))
            .ThenBy(row => row[0])
            .First();
        foreach (var index in chosen)
        {
            var middle = items[index].Left + items[index].Width / 2;
            if (point.X < middle)
            {
                return index;
            }
        }
        return chosen[^1] + 1;
    }

    private static double Distance(double y, double top, double bottom) =>
        y < top ? top - y : y > bottom ? y - bottom : 0;
}
