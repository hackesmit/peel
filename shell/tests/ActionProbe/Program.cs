// Runs one Alt wheel action in the logged on desktop session and writes what
// it did to a result file, so run-actions.ps1 can check it from hqbox over
// ssh (where there is no desktop). It drives the real action classes, the same
// ones the app uses, never a copy of their logic.
//
// copy     set the clipboard to the files, read the file drop list back
// terminal start the terminal, report the process, then kill it
// move      move the files into --dest, report where they landed
// float     pin the thumbnails, screenshot the desktop to --shot
// share     raise the share sheet, screenshot the desktop to --shot
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Peel.Actions;
using Peel.Engine;
using Peel.Jobs;

namespace Peel.ActionProbe;

public sealed class Probe : Application
{
    private static Dictionary<string, string> _args = new();

    [STAThread]
    public static int Main(string[] argv)
    {
        _args = Parse(argv);
        var app = new Probe();
        app.Run();
        return 0;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var id = _args.GetValueOrDefault("action", "");
        var files = (_args.GetValueOrDefault("files", "") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries).ToArray();
        var outPath = _args.GetValueOrDefault("out", "result.txt");
        var report = new StringBuilder();
        try
        {
            switch (id)
            {
                case "copy":
                    Copy(files, report);
                    Finish(outPath, report);
                    break;
                case "terminal":
                    Terminal(files, report);
                    Finish(outPath, report);
                    break;
                case "move":
                    Move(files, _args.GetValueOrDefault("dest", ""), report);
                    Finish(outPath, report);
                    break;
                case "float":
                    ShowThenShoot(() => { FloatAction.Run(files, Jobs()); return null; }, files, report, outPath);
                    return;
                case "share":
                    ShowThenShoot(() => ShareAction.TryShow(files, out var reason) ? null : reason, files, report, outPath);
                    return;
                default:
                    report.AppendLine($"FAIL unknown action {id}");
                    Finish(outPath, report);
                    break;
            }
        }
        catch (Exception error)
        {
            report.AppendLine($"FAIL {id} threw: {error}");
            Finish(outPath, report);
        }
    }

    private JobRunner Jobs() => new(new EngineClient(), new ToastHost(), Dispatcher.CurrentDispatcher);

    private void Copy(string[] files, StringBuilder report)
    {
        CopyAction.SetOnce(files);
        var read = System.Windows.Forms.Clipboard.GetFileDropList().Cast<string>().ToArray();
        var ok = read.SequenceEqual(files, StringComparer.OrdinalIgnoreCase);
        report.AppendLine($"{(ok ? "PASS" : "FAIL")} copy: clipboard holds {read.Length} file(s)");
        foreach (var path in read)
        {
            report.AppendLine($"  {path}");
        }
    }

    private void Terminal(string[] files, StringBuilder report)
    {
        var folder = TerminalAction.FolderFor(files);
        Process? process = null;
        var which = "wt.exe";
        try
        {
            process = Process.Start(TerminalAction.Primary(folder));
        }
        catch
        {
            which = "powershell.exe";
            process = Process.Start(TerminalAction.Fallback(folder));
        }
        if (process is null)
        {
            report.AppendLine("FAIL terminal: no process started");
            return;
        }
        System.Threading.Thread.Sleep(800);
        report.AppendLine($"PASS terminal: started {which} pid {process.Id} for {folder}");
        try
        {
            // wt.exe hands off to a WindowsTerminal.exe host and exits, so kill
            // the terminal window by its host rather than the launcher.
            foreach (var host in Process.GetProcessesByName("WindowsTerminal"))
            {
                host.Kill(entireProcessTree: true);
            }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error)
        {
            report.AppendLine($"  note: could not kill the terminal: {error.Message}");
        }
    }

    private void Move(string[] files, string dest, StringBuilder report)
    {
        if (string.IsNullOrEmpty(dest))
        {
            report.AppendLine("FAIL move: no --dest");
            return;
        }
        Directory.CreateDirectory(dest);
        var outcome = MoveAction.MoveAll(files, dest);
        var allThere = outcome.Destinations.All(p => File.Exists(p) || Directory.Exists(p));
        var sourcesGone = files.All(p => !File.Exists(p) && !Directory.Exists(p));
        var ok = outcome.Failures.Count == 0 && allThere && sourcesGone;
        report.AppendLine($"{(ok ? "PASS" : "FAIL")} move: {outcome.Moved} of {files.Length} into {dest}");
        foreach (var path in outcome.Destinations)
        {
            report.AppendLine($"  {path}");
        }
        foreach (var failure in outcome.Failures)
        {
            report.AppendLine($"  failure: {failure}");
        }
    }

    private void ShowThenShoot(Func<string?> run, string[] files, StringBuilder report, string outPath)
    {
        // Float windows and the share sheet open at the cursor. In an
        // unattended run the cursor is wherever it was left, so centre it on
        // the primary screen first; then they land where the screenshot shows.
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        System.Windows.Forms.Cursor.Position =
            new System.Drawing.Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        var failure = run();
        if (failure is not null)
        {
            report.AppendLine($"FAIL: {failure}");
        }
        var shot = _args.GetValueOrDefault("shot", "shot.png");
        var waitMs = int.TryParse(_args.GetValueOrDefault("wait", "2500"), out var w) ? w : 2500;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Screenshot(shot);
                report.AppendLine($"PASS screenshot written to {shot} for {files.Length} file(s)");
            }
            catch (Exception error)
            {
                report.AppendLine($"FAIL screenshot: {error.Message}");
            }
            Finish(outPath, report);
        };
        timer.Start();
    }

    private static void Screenshot(string path)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void Finish(string outPath, StringBuilder report)
    {
        try
        {
            File.WriteAllText(outPath, report.ToString());
        }
        catch
        {
            // Nothing to do; the exit code and the missing file are the signal.
        }
        Shutdown();
    }

    private static Dictionary<string, string> Parse(string[] argv)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (argv.Length > 0 && !argv[0].StartsWith("--"))
        {
            result["action"] = argv[0];
        }
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (argv[i].StartsWith("--"))
            {
                result[argv[i][2..]] = argv[i + 1];
            }
        }
        return result;
    }
}
