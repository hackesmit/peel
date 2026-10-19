// A stand in for the `peel` CLI, used by the wheel rendering test and the
// engine client unit tests.
//
// The registry on a developer machine holds whatever families have landed so
// far, which may be one conversion. The wheel has to be right with ten of them
// too, and with more than fit on one ring. This prints a canned `peel options`
// payload so a screenshot can show that, and refuses `peel run`, so nothing it
// is pointed at can ever write a file.
//
// Peel finds it through PEEL_CLI; the payload comes from PEEL_FAKE_OPTIONS.
// PEEL_FAKE_MODE switches it into a test behaviour instead:
//   echo    answer with the paths it was given (inline and --files-from), in
//           order, and the list file path in "message"
//   hang    write its pid to PEEL_FAKE_PIDS, start a child that does the same,
//           and sleep: an engine that never finishes
//   orphan  like hang, but print a result and exit at once, leaving the child
//           holding the inherited stdout and stderr pipes open
// It is a test tool: it is in Peel.sln only so the unit tests build it in the
// same configuration, and it is never installed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

if (args.Length > 0 && args[0] == "sleep")
{
    RecordPid();
    Thread.Sleep(TimeSpan.FromMinutes(2));
    return 0;
}

var mode = Environment.GetEnvironmentVariable("PEEL_FAKE_MODE") ?? string.Empty;
if (mode == "echo")
{
    var files = new List<string>();
    var listFile = string.Empty;
    var skip = args.Length > 0 && args[0] == "run" ? 2 : 1;
    for (var i = skip; i < args.Length; i++)
    {
        if (args[i] == "--files-from" && i + 1 < args.Length)
        {
            listFile = args[++i];
            // The engine's own rule: "\n" ends a line, one trailing "\r" is
            // dropped, blank lines are skipped, nothing else splits a path.
            files.InsertRange(0, File.ReadAllText(listFile).Split('\n')
                .Select(line => line.EndsWith('\r') ? line[..^1] : line)
                .Where(line => line.Trim().Length > 0));
        }
        else if (args[i] == "--param" && i + 1 < args.Length)
        {
            i++;
        }
        else
        {
            files.Add(args[i]);
        }
    }
    Console.Out.Write(JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["ok"] = true,
        ["files"] = files,
        ["outputs"] = new List<string>(),
        ["wheels"] = new List<object>(),
        ["message"] = listFile,
    }));
    return 0;
}
if (mode is "hang" or "orphan")
{
    RecordPid();
    // No redirection: the child inherits this process's stdout and stderr,
    // which are the pipes Peel reads.
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "sleep") { UseShellExecute = false });
    WaitForPids(2);
    if (mode == "orphan")
    {
        Console.Out.Write("""{"ok": true, "outputs": [], "message": "the engine is done, its backend is not"}""");
        Console.Out.Flush();
        return 0;
    }
    Thread.Sleep(TimeSpan.FromMinutes(2));
    return 0;
}

if (args.Length > 0 && args[0] == "options")
{
    var path = Environment.GetEnvironmentVariable("PEEL_FAKE_OPTIONS");
    if (path is not null && File.Exists(path))
    {
        Console.Out.Write(File.ReadAllText(path));
        return 0;
    }
    Console.Out.Write("""{"ok": false, "outputs": [], "message": "PEEL_FAKE_OPTIONS is not set", "code": "usage"}""");
    return 1;
}

Console.Out.Write("""{"ok": false, "outputs": [], "message": "the fake engine never runs anything", "code": "fake"}""");
return 1;

static void RecordPid()
{
    var file = Environment.GetEnvironmentVariable("PEEL_FAKE_PIDS");
    if (file is null)
    {
        return;
    }
    for (var i = 0; i < 50; i++)
    {
        try
        {
            File.AppendAllText(file, Environment.ProcessId + Environment.NewLine);
            return;
        }
        catch (IOException)
        {
            Thread.Sleep(20);
        }
    }
}

static void WaitForPids(int count)
{
    var file = Environment.GetEnvironmentVariable("PEEL_FAKE_PIDS");
    for (var i = 0; file is not null && i < 100; i++)
    {
        try
        {
            if (File.Exists(file) && File.ReadAllLines(file).Count(l => l.Trim().Length > 0) >= count)
            {
                return;
            }
        }
        catch (IOException)
        {
        }
        Thread.Sleep(50);
    }
}
