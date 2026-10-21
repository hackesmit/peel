// Which PDF entries have a window here, and how each one opens: the engine
// draws the thumbnails first (PageThumbs), and a PDF it cannot draw is a
// toast with its reason rather than an empty grid.
using System.Threading;
using System.Threading.Tasks;
using Peel.Engine;

namespace Peel.Editors.Pages;

internal static class PageEditors
{
    internal static bool Handles(string id) => id is "pdf.organize" or "pdf.split";

    internal static async Task<(EditorWindowBase? Window, string Problem)> OpenAsync(
        ActionEntry entry, string path, ParamSchema schema, EngineClient engine)
    {
        var (pages, problem) = await PageThumbs.LoadAsync(engine, path, CancellationToken.None).ConfigureAwait(true);
        if (pages is null)
        {
            return (null, problem);
        }
        Log.Write($"{entry.Id}: {pages.Count} page thumbnail(s) for {path}");
        return (new PagesEditor(entry, path, pages, schema), string.Empty);
    }
}
