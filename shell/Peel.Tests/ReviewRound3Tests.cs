// Final review (round 3) checks for hq-plct.7, written by the fresh-context
// reviewer. They target the new ContainedProcess launcher, the per hook
// liveness, and the list file sweep.
//
// The Python checks run a real python.exe (the C runtime's own argv parser,
// not .NET's). Set PEEL_REVIEW3_PYTHON to a python.exe; without it they fall
// back to C:\peel-dev\review2-4500\venv\Scripts\python.exe and fail loudly if
// that is missing, so a green run always means Python really parsed the line.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Peel.Engine;
using Peel.Hooks;
using Peel.Jobs;
using Xunit;

namespace Peel.Tests;

[Collection("environment")]
public sealed class ReviewRound3Tests : IDisposable
{
    private readonly string? _previousCli = Environment.GetEnvironmentVariable("PEEL_CLI");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"peel-review3-{Guid.NewGuid():N}");

    public ReviewRound3Tests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PEEL_CLI", _previousCli);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", null);
        Environment.SetEnvironmentVariable("PEEL_REVIEW3_MARK", null);
        try { Directory.Delete(_scratch, recursive: true); } catch (Exception) { }
    }

    private static string Python
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("PEEL_REVIEW3_PYTHON");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = @"C:\peel-dev\review2-4500\venv\Scripts\python.exe";
            }
            Assert.True(File.Exists(path), $"no python.exe at {path}");
            return path;
        }
    }

    /// <summary>Run python.exe through ContainedProcess and return its stdout.</summary>
    private async Task<string> RunPython(string script, IReadOnlyList<string> extra, IReadOnlyDictionary<string, string>? env = null)
    {
        var file = Path.Combine(_scratch, $"s{Guid.NewGuid():N}.py");
        File.WriteAllText(file, script, new UTF8Encoding(false));
        var arguments = new List<string> { "-I", "-X", "utf8", file };
        arguments.AddRange(extra);
        using var job = ProcessJob.Create();
        Assert.NotNull(job);
        using var process = ContainedProcess.Start(Python, arguments,
            env ?? new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" }, job!);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync(limit.Token);
        var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync(limit.Token);
        await process.WaitForExitAsync(limit.Token);
        var stdout = await output;
        var stderr = await errors;
        Assert.True(process.ExitCode == 0, $"python exit {process.ExitCode}: {stderr}");
        return stdout;
    }

    // ---- Quoting: the MSVCRT parser in python.exe gets every argument back ----

    public static readonly string[] Hostile =
    {
        "",
        " ",
        "\t",
        "a\tb",
        @"C:\trailing\",
        @"C:\two\\",
        @"C:\a b\",
        @"C:\a b\\",
        "\\",
        "\\\\",
        "\"",
        "\"\"",
        "a\"b",
        "a\\\"b",
        "a\\\\\"b",
        "\\\"",
        "\"\\",
        "end\\\\\\",
        "-c",
        "-m",
        "--files-from",
        "/c",
        "/?",
        "a&calc&b",
        "%PATH%",
        "^caret^",
        "ñandú 🍊",
        "\u2028x\u0085",
        "x\ny",
        "x\ry",
        "  leading and trailing  ",
        @"C:\Users\danie\Documents\""quoted"" name.txt",
        "\\\\server\\share\\dir\\",
        "*?<>|",
        "'single'",
    };

    [Fact]
    public async Task PythonReceivesEveryHostileArgumentExactly()
    {
        var stdout = await RunPython(
            "import sys, json\nsys.stdout.write(json.dumps(sys.argv[1:]))\n",
            Hostile);
        var back = JsonSerializer.Deserialize<List<string>>(stdout)!;
        Assert.Equal(Hostile.ToList(), back);
    }

    [Fact]
    public void EveryArgumentRoundTripsThroughCommandLineToArgvW()
    {
        var line = ContainedProcess.CommandLine(@"C:\x y\e.exe", Hostile);
        var pointer = CommandLineToArgvW(line, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var parsed = new List<string>();
            for (var i = 1; i < count; i++)
            {
                parsed.Add(System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!);
            }
            Assert.Equal(Hostile.ToList(), parsed);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    // ---- Environment block ----

    [Fact]
    public async Task PythonSeesTheInheritedEnvironmentAndTheOverrides()
    {
        Environment.SetEnvironmentVariable("PEEL_REVIEW3_MARK", "inherited ñ 🍊 = value");
        var stdout = await RunPython(
            "import os, sys, json\nsys.stdout.write(json.dumps(dict(os.environ)))\n",
            Array.Empty<string>(),
            new Dictionary<string, string>
            {
                ["PYTHONIOENCODING"] = "utf-8",
                ["PEEL_REVIEW3_OVERRIDE"] = "über 🍊",
                ["path"] = Environment.GetEnvironmentVariable("PATH") + ";C:\\review3",
            });
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(stdout)!;
        var upper = env.ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value);
        Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), upper["SYSTEMROOT"]);
        Assert.EndsWith(";C:\\review3", upper["PATH"]);
        Assert.Equal("inherited ñ 🍊 = value", upper["PEEL_REVIEW3_MARK"]);
        Assert.Equal("über 🍊", upper["PEEL_REVIEW3_OVERRIDE"]);
        // One PATH only: a lowercase override replaced the inherited Path.
        Assert.Single(env.Keys, k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheEnvironmentBlockIsSortedAndDoubleNulTerminated()
    {
        var block = ContainedProcess.EnvironmentBlock(new Dictionary<string, string> { ["zz_last"] = "1", ["AA_FIRST"] = "2" });
        // Ordinal: a culture comparison ignores NUL and matches anywhere.
        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
        Assert.False(block[..^1].Contains("\0\0", StringComparison.Ordinal));
        var names = block.TrimEnd('\0').Split('\0').Select(e => e[..e.IndexOf('=', 1)]).ToList();
        Assert.Equal(names.OrderBy(n => n.ToUpperInvariant(), StringComparer.Ordinal).ToList(), names);
        Assert.Contains("SystemRoot", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("PATH", names, StringComparer.OrdinalIgnoreCase);
    }

    // ---- Handle inheritance and leaks ----

    [Fact]
    public async Task OnlyTheThreePipesAreInherited()
    {
        // An inheritable pipe Peel holds while it starts an engine (another
        // drag's pipes, say). Inherited handles keep their value, so the child
        // checks whether that value is a pipe in its own table.
        using var decoy = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, HandleInheritability.Inheritable);
        var value = decoy.ClientSafePipeHandle.DangerousGetHandle().ToInt64();
        var stdout = await RunPython(
            "import sys, ctypes\n"
            + "from ctypes import wintypes\n"
            + "k = ctypes.WinDLL('kernel32', use_last_error=True)\n"
            + "k.GetFileType.argtypes = [wintypes.HANDLE]\n"
            + "kind = k.GetFileType(wintypes.HANDLE(int(sys.argv[1])))\n"
            + "sys.stdout.write('open' if kind == 3 else 'closed')\n",
            new[] { value.ToString() });
        Assert.Equal("closed", stdout);
    }

    [Fact]
    public async Task TwoHundredRunsLeakNoHandles()
    {
        Environment.SetEnvironmentVariable("PEEL_CLI", EngineProcessTests.FakeEngine);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        var engine = new EngineClient();
        var files = new List<string> { @"C:\a b\c.txt" };
        // Warm up: JIT, thread pool, lazy location.
        for (var i = 0; i < 10; i++)
        {
            Assert.True((await engine.OptionsAsync(files)).Ok);
        }
        Settle();
        var before = Process.GetCurrentProcess().HandleCount;
        for (var i = 0; i < 200; i++)
        {
            var result = await engine.OptionsAsync(files);
            Assert.True(result.Ok, result.Message);
        }
        Settle();
        var after = Process.GetCurrentProcess().HandleCount;
        // Each run opens at least 7 handles (job, process, thread, 6 pipe
        // ends); a leak of even one per run shows as 200.
        Assert.True(after - before < 40, $"handles before {before}, after {after}");
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(200);
        }
    }

    // ---- Liveness: no reinstall churn while the user types ----

    [Fact]
    public void ContinuousTypingWithAStillMouseNeverReinstalls()
    {
        var liveness = new HookLiveness();
        uint start = 1_000_000;
        const uint mouseLast = 1_000_000;
        var reasons = new List<string>();
        // 30 minutes of a key every 100 ms, sampled each 1 s (App's timer
        // interval or faster), with the mouse untouched.
        for (uint t = 0; t <= 30 * 60 * 1000; t += 1000)
        {
            var now = start + t;
            var reason = liveness.Check(new HookSample(now, true, now - 50, mouseLast, now - 50, false));
            if (reason is not null)
            {
                reasons.Add($"{t}: {reason}");
            }
        }
        Assert.Empty(reasons);
    }

    [Fact]
    public void ContinuousMouseWorkWithNoKeysNeverReinstalls()
    {
        var liveness = new HookLiveness();
        uint start = 5;
        var reasons = new List<string>();
        for (uint t = 0; t <= 30 * 60 * 1000; t += 1000)
        {
            var now = start + t;
            var reason = liveness.Check(new HookSample(now, true, now - 20, now - 20, start, true));
            if (reason is not null)
            {
                reasons.Add($"{t}: {reason}");
            }
        }
        Assert.Empty(reasons);
    }

    [Fact]
    public void TickWrapDoesNotTriggerAReinstallWhileTyping()
    {
        var liveness = new HookLiveness();
        var start = uint.MaxValue - 30_000;
        var reasons = new List<string>();
        for (uint t = 0; t <= 120_000; t += 1000)
        {
            var now = unchecked(start + t);
            var reason = liveness.Check(new HookSample(now, true, unchecked(now - 50), start, unchecked(now - 50), false));
            if (reason is not null)
            {
                reasons.Add($"{t}: {reason}");
            }
        }
        Assert.Empty(reasons);
    }

    // ---- Scavenging ----

    [Fact]
    public void TheSweepTouchesOnlyOldFilesStarTxt()
    {
        var folder = Path.Combine(_scratch, "Peel");
        Directory.CreateDirectory(folder);
        var old = DateTime.UtcNow.AddDays(-2);
        string Make(string name, DateTime when)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, when);
            return path;
        }
        var stale = Make("files-0123.txt", old);
        var fresh = Make("files-4567.txt", DateTime.UtcNow);
        var otherName = Make("notes-0123.txt", old);
        var longerExtension = Make("files-89ab.txtx", old);
        var noDash = Make("files.txt", old);
        var sub = Directory.CreateDirectory(Path.Combine(folder, "files-sub.txt"));
        Directory.SetLastWriteTimeUtc(sub.FullName, old);
        var nested = Path.Combine(folder, "deeper");
        Directory.CreateDirectory(nested);
        var nestedStale = Path.Combine(nested, "files-cdef.txt");
        File.WriteAllText(nestedStale, "x");
        File.SetLastWriteTimeUtc(nestedStale, old);

        Assert.Equal(1, EngineClient.ScavengeListFiles(TimeSpan.FromDays(1), folder));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(otherName));
        Assert.True(File.Exists(longerExtension), "the 3 letter extension quirk matched files-*.txtx");
        Assert.True(File.Exists(noDash));
        Assert.True(Directory.Exists(sub.FullName));
        Assert.True(File.Exists(nestedStale));
    }
}
