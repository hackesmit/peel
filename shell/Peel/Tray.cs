// The tray icon: what Peel looks like when it is doing nothing, which is most
// of the time. Quit and Open log are the two that matter; recent jobs is there
// because a conversion that finished while you were looking elsewhere should
// still be findable.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Peel.Jobs;
using Forms = System.Windows.Forms;

namespace Peel;

internal sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly JobRunner _jobs;
    private readonly Func<string> _status;

    internal Tray(JobRunner jobs, Func<string> status, Action quit)
    {
        _jobs = jobs;
        _status = status;
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => Build(menu, quit);
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Peel",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Left click shows the same menu: Peel has no main window.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                menu.Show(Forms.Cursor.Position);
            }
        };
    }

    internal void Notify(string title, string message) =>
        Log.Write($"tray balloon: {title}: {message}");

    private void Build(Forms.ContextMenuStrip menu, Action quit)
    {
        menu.Items.Clear();
        var header = new Forms.ToolStripMenuItem($"Peel {Version()}") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Forms.ToolStripMenuItem(_status()) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var recent = new Forms.ToolStripMenuItem("Recent jobs");
        var records = _jobs.Recent;
        if (records.Count == 0)
        {
            recent.DropDownItems.Add(new Forms.ToolStripMenuItem("Nothing yet") { Enabled = false });
        }
        else
        {
            foreach (var record in records)
            {
                var text = $"{record.When:HH:mm} {record.Label}: {record.Message}";
                var item = new Forms.ToolStripMenuItem(Shorten(text));
                var outputs = record.Outputs;
                item.Enabled = outputs.Count > 0;
                item.Click += (_, _) => Reveal(outputs);
                recent.DropDownItems.Add(item);
            }
        }
        menu.Items.Add(recent);

        var log = new Forms.ToolStripMenuItem("Open log");
        log.Click += (_, _) => OpenLog();
        menu.Items.Add(log);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem("Quit Peel");
        exit.Click += (_, _) => quit();
        menu.Items.Add(exit);
    }

    private static string Shorten(string text) => text.Length <= 70 ? text : text[..67] + "...";

    private static string Version() =>
        typeof(Tray).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static void Reveal(IReadOnlyList<string> outputs)
    {
        try
        {
            var first = outputs.FirstOrDefault();
            if (first is not null && (File.Exists(first) || Directory.Exists(first)))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
            }
        }
        catch (Exception error)
        {
            Log.Write("cannot reveal the output", error);
        }
    }

    private static void OpenLog()
    {
        try
        {
            Log.Flush();
            if (!File.Exists(Log.Path))
            {
                File.WriteAllText(Log.Path, string.Empty);
            }
            Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Log.Write("cannot open the log", error);
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "peel.ico");
            if (File.Exists(path))
            {
                return new System.Drawing.Icon(path);
            }
        }
        catch (Exception error)
        {
            Log.Write("cannot load the tray icon", error);
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
