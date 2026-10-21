// The thumbnails of a PDF's pages, rendered by the engine.
//
// `peel run docs.pdf_to_png --param dpi=36 --out-dir <temp>` writes one PNG
// per page in page order, at the engine's lowest resolution: a thumbnail, not
// a page to read. Rendering through the engine rather than a Windows PDF API
// means the page the user sees is the page PyMuPDF will move, rotation already
// applied, and a PDF the engine cannot open fails here, before the user has
// arranged anything. The count of pictures is the page count.
//
// The PNGs are decoded into frozen bitmaps and the folder is deleted before
// this returns, so nothing is left in %TEMP% or beside the user's file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Peel.Engine;

namespace Peel.Editors.Pages;

internal static class PageThumbs
{
    internal const string Action = "docs.pdf_to_png";

    /// <summary>The engine's lowest DPI, which is plenty for a thumbnail.</summary>
    internal const int Dpi = 36;

    /// <summary>Pages decoded no taller than this many pixels.</summary>
    internal const int Height = 220;

    internal static async Task<(IReadOnlyList<BitmapSource>? Pages, string Problem)> LoadAsync(
        EngineClient engine, string path, CancellationToken token)
    {
        string? folder = null;
        try
        {
            folder = Path.Combine(Path.GetTempPath(), "Peel", $"pages-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var parameters = new Dictionary<string, string> { ["dpi"] = Dpi.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var result = await engine.RunAsync(Action, new[] { path }, parameters, token, folder).ConfigureAwait(false);
            if (!result.Ok || result.Outputs.Count == 0)
            {
                var message = result.Message.Length > 0 ? result.Message : "the engine drew no page";
                return (null, $"Peel cannot show the pages of {Path.GetFileName(path)}: {message}");
            }
            var pages = await Task.Run(() => result.Outputs.Select(Decode).ToList(), token).ConfigureAwait(false);
            return (pages, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (null, "cancelled");
        }
        catch (Exception error)
        {
            Log.Write($"pages: cannot render {path}", error);
            return (null, $"Peel cannot show the pages of {Path.GetFileName(path)}: {error.Message}");
        }
        finally
        {
            Discard(folder);
        }
    }

    private static BitmapSource Decode(string png)
    {
        // A stream, not a Uri: the PNG is named after the user's file.
        using var stream = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelHeight = Height;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void Discard(string? folder)
    {
        if (folder is null)
        {
            return;
        }
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error)
        {
            Log.Write($"pages: cannot delete the temporary folder {folder}", error);
        }
    }
}
