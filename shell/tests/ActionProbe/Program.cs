// Runs one Alt wheel action in the logged on desktop session and writes what
// it did to a result file, so run-actions.ps1 can check it from hqbox over
// ssh (where there is no desktop). It drives the real action classes, the same
// ones the app uses, never a copy of their logic.
//
// copy     set the clipboard to the files, read the file drop list back
// copypath run the branch engine's files.copy_path, put its text on the
//          clipboard with the app's ClipboardWriter, read the text back
// terminal start the terminal, require exactly one new shell whose current
//          directory is the folder and nothing injected, then kill what it started
// move      move the files into --dest, report where they landed
// float     pin the thumbnails, screenshot the desktop to --shot
// share     raise the share sheet, screenshot the desktop to --shot, dismiss it
// clean    dismiss any share sheet on the desktop
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
            .Split('|', StringSplitOptions.RemoveEmptyEntries).ToArray();
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
                case "copypath":
                    CopyPath(files, report);
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
                    ShowThenShoot(() => ShareAction.TryShow(files, out var reason) ? null : reason, files, report, outPath,
                        after: () =>
                        {
                            // Never leave the sheet on Daniel's desktop.
                            var left = ShareSheet.CloseAll();
                            report.AppendLine($"{(left == 0 ? "PASS" : "FAIL")} share sheet dismissed, {left} left on screen");
                        });
                    return;
                case "clean":
                    {
                        var left = ShareSheet.CloseAll();
                        report.AppendLine($"{(left == 0 ? "PASS" : "FAIL")} clean: {left} share sheet(s) left on screen");
                        Finish(outPath, report);
                    }
                    break;
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

    /// <summary>
    /// Copy Path end to end: the engine named by --python and --engine (the
    /// branch under test, not whatever is installed) produces the text, the
    /// app's own ClipboardWriter puts it on the clipboard, and the clipboard
    /// is read back and compared with the expected form built here
    /// independently: every path in double quotes, joined with CRLF.
    /// </summary>
    private void CopyPath(string[] files, StringBuilder report)
    {
        var python = _args.GetValueOrDefault("python", "python.exe")!;
        var engineRoot = _args.GetValueOrDefault("engine", "")!;
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.Environment["PYTHONPATH"] = engineRoot;
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in new[] { "-m", "peel_engine.cli", "run", "files.copy_path" }.Concat(files))
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        string? text = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            text = doc.RootElement.GetProperty("data").GetProperty("clipboard_text").GetString();
        }
        catch (Exception parse)
        {
            report.AppendLine($"FAIL copypath: engine output unreadable ({parse.Message}) {error.Trim()}");
            return;
        }
        ClipboardWriter.SetOnce(text!);
        var read = System.Windows.Forms.Clipboard.GetText();
        var expected = string.Join("\r\n", files.Select(f => "\"" + Path.GetFullPath(f) + "\""));
        var ok = read == expected;
        report.AppendLine($"{(ok ? "PASS" : "FAIL")} copypath: clipboard text has {read.Split("\r\n").Length} quoted CRLF line(s)");
        foreach (var line in read.Split("\r\n"))
        {
            report.AppendLine($"  {line}");
        }
        if (!ok)
        {
            report.AppendLine($"  expected: {expected.Replace("\r\n", "<CRLF>")}");
            report.AppendLine($"  read:     {read.Replace("\r", "<CR>").Replace("\n", "<LF>")}");
        }
    }

    // Shells a Windows Terminal tab can host. One of these is the tab's shell;
    // a second one, or a calculator, means a command was injected.
    private static readonly string[] ShellNames =
        { "powershell", "pwsh", "cmd", "wsl", "bash", "ubuntu", "nu" };

    private static readonly string[] InjectedNames =
        { "calc", "CalculatorApp", "win32calc" };

    /// <summary>
    /// Terminal end to end. Records every process before the launch, starts
    /// the real TerminalAction start info, waits, and then requires exactly
    /// one new shell process, whose current directory (read from its PEB) is
    /// the dropped folder, and no calculator. A folder named
    /// "safe; new-tab powershell -Command calc" proves Windows Terminal's ';'
    /// grammar never sees the path: injection would add a second shell and a
    /// calculator. Only processes this probe started are killed.
    /// </summary>
    private void Terminal(string[] files, StringBuilder report)
    {
        var folder = TerminalAction.FolderFor(files);
        var self = Environment.ProcessId;
        var before = new HashSet<int>(Process.GetProcesses().Select(p => p.Id));
        Process? process = null;
        var which = "wt.exe";
        try
        {
            // --control-unsafe is a negative control for this check, run by
            // hand only: the old "wt -d <folder>" form, which a ';' in the
            // folder name can turn into a second command.
            var info = TerminalAction.Primary(folder);
            if (_args.ContainsKey("control-unsafe"))
            {
                which = "wt.exe -d <folder> (negative control)";
                info = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                info.ArgumentList.Add("-d");
                info.ArgumentList.Add(folder);
            }
            process = Process.Start(info);
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
        System.Threading.Thread.Sleep(int.TryParse(_args.GetValueOrDefault("settle", "4000"), out var ms) ? ms : 4000);
        // Only this desktop session: other tools on George (ssh sessions in
        // session 0) start their own powershell and conhost all the time.
        var session = Process.GetCurrentProcess().SessionId;
        var fresh = Process.GetProcesses()
            .Where(p => !before.Contains(p.Id) && p.Id != self && SessionOf(p) == session)
            .ToList();
        var shells = fresh.Where(p => ShellNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
        var injected = fresh.Where(p => InjectedNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
        var want = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var cwds = shells.Select(p => (p, cwd: CurrentDirectoryOf(p))).ToList();
        var inFolder = cwds.Count(c => c.cwd is not null
            && string.Equals(Path.TrimEndingDirectorySeparator(c.cwd), want, StringComparison.OrdinalIgnoreCase));
        var ok = shells.Count == 1 && inFolder == 1 && injected.Count == 0;
        report.AppendLine($"{(ok ? "PASS" : "FAIL")} terminal: {which} for {folder}: {shells.Count} new shell(s), {inFolder} in the folder, {injected.Count} injected process(es)");
        foreach (var (p, cwd) in cwds)
        {
            report.AppendLine($"  shell {p.ProcessName} pid {p.Id} cwd {cwd ?? "(unreadable)"}");
        }
        report.AppendLine($"  new processes: {string.Join(", ", fresh.Select(p => p.ProcessName).Distinct())}");
        // Kill what this probe started: the new terminal hosts, the shells
        // (which closes a tab opened in a window the user already had) and
        // anything injected. Never a process that was running before.
        foreach (var p in fresh.Where(p =>
                     ShellNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)
                     || InjectedNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)
                     || p.ProcessName is "WindowsTerminal" or "OpenConsole" or "wt"))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception error) { report.AppendLine($"  note: could not kill {p.ProcessName}: {error.Message}"); }
        }
    }

    private static int SessionOf(Process process)
    {
        try { return process.SessionId; } catch { return -1; }
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>
    /// A 64 bit process's current directory, from its PEB:
    /// PEB+0x20 ProcessParameters, RTL_USER_PROCESS_PARAMETERS+0x38
    /// CurrentDirectory.DosPath (UNICODE_STRING). Null when unreadable.
    /// </summary>
    private static string? CurrentDirectoryOf(Process process)
    {
        try
        {
            var handle = process.Handle;
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf(info), out _) != 0)
            {
                return null;
            }
            var pointer = new byte[8];
            if (!ReadProcessMemory(handle, info.PebBaseAddress + 0x20, pointer, 8, out _)) return null;
            var parameters = (IntPtr)BitConverter.ToInt64(pointer, 0);
            var unicode = new byte[16];
            if (!ReadProcessMemory(handle, parameters + 0x38, unicode, 16, out _)) return null;
            var length = BitConverter.ToUInt16(unicode, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(unicode, 8);
            var text = new byte[length];
            if (!ReadProcessMemory(handle, buffer, text, length, out _)) return null;
            return Encoding.Unicode.GetString(text);
        }
        catch
        {
            return null;
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

    private void ShowThenShoot(Func<string?> run, string[] files, StringBuilder report, string outPath, Action? after = null)
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
            after?.Invoke();
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
