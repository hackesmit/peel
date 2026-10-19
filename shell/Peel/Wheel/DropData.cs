// What a drag carries, read from its OLE data object.
//
// Peel takes file paths and nothing else: FileDrop (CF_HDROP), which Explorer,
// the desktop and most file managers offer. A drag that only offers
// FileContents (Outlook attachments, some browser downloads) has no path the
// engine could open, and supporting it would mean writing the stream to a
// temporary file first; that is deliberately out of scope, so such a drag is
// treated like text and the wheel steps aside.
using System;
using System.Linq;
using System.Windows;

namespace Peel.Wheel;

internal static class DropData
{
    /// <summary>The file paths the data carries, or null when it carries none.</summary>
    internal static string[]? FilePaths(IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            return paths is { Length: > 0 } && paths.All(p => !string.IsNullOrEmpty(p)) ? paths : null;
        }
        catch (Exception error)
        {
            // A source can refuse to render a format it advertised.
            Log.Write("cannot read the drag data", error);
            return null;
        }
    }

    /// <summary>The formats on offer, for the log.</summary>
    internal static string Describe(IDataObject? data)
    {
        try
        {
            var formats = data?.GetFormats(false) ?? Array.Empty<string>();
            return formats.Length == 0 ? "no formats" : string.Join(",", formats.Take(8));
        }
        catch (Exception)
        {
            return "formats unreadable";
        }
    }
}
