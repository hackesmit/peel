// Runs the `peel` CLI and parses its one JSON object.
//
// Three rules hold everywhere in here:
//   1. Never block the UI thread. Every call is async and the caller awaits it
//      off the wheel's paint path.
//   2. A failure is a message, never an exception reaching the user: whatever
//      goes wrong comes back as a result with ok=false and a code, which the
//      toast shows as written.
//   3. The child process gets UTF-8 forced on stdout, because Python under a
//      redirected pipe on Windows otherwise picks the ANSI code page and
//      mangles (or fails on) non ASCII file names.
//
// Every engine process runs in a kill on close job object of its own
// (Jobs/ProcessJob.cs). It is created suspended and resumed only once it is in
// the job and registered for KillAll (Jobs/ContainedProcess.cs); if either
// fails, the engine is not run and the call returns an error. The timeout
// covers the process exit and the draining of both pipes, because a backend
// the engine started can inherit the pipes and keep them open after the
// engine has exited; on expiry the whole job is terminated, which closes the
// pipes and lets the readers finish.
//
// The whole call, list file and process creation included, runs on the thread
// pool. The wheel calls OptionsAsync from inside an OLE drag callback, and an
// async method runs synchronously up to its first real await, so without the
// hop a slow %TEMP% or a slow CreateProcess would stall Explorer's drag.
//
// Windows caps a command line at 32767 characters. A drag of a few hundred
// photos is longer than that, so past CommandLineBudget the paths go to a
// temporary UTF-8 list file passed as --files-from, deleted afterwards (and,
// should Peel die first, swept at the next start by ScavengeListFiles).
//
// Editor parameters take the same road for a different reason. `peel run
// --param K=V` reads the value from a file when it starts with "@", which is
// how the engine lets an editor pass a long list of shapes; that also means a
// Metadata title of "@danielfhack" would be read as a file name and the job
// would fail. So whenever a value starts with "@", or the parameters are long
// (a pen stroke is hundreds of points), they all go into a temporary JSON
// object passed as --params-file, where no value is magic.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Peel.Hooks;
using Peel.Jobs;

namespace Peel.Engine;

internal sealed record EngineLocation(string FileName, IReadOnlyList<string> Prefix, string Description);

internal sealed class EngineClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Lazy<EngineLocation?> _location;
    private readonly ConcurrentDictionary<ProcessJob, byte> _live = new();
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _closed;

    internal EngineClient() => _location = new Lazy<EngineLocation?>(Locate, isThreadSafe: true);

    internal TimeSpan OptionsTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Past this many characters the file paths go in a list file. Windows
    /// allows 32767; the margin covers the executable, the prefix and quoting.
    /// </summary>
    internal int CommandLineBudget { get; init; } = 24000;

    /// <summary>
    /// Past this many characters of parameters they go in a JSON file. Well
    /// under CommandLineBudget, which the file paths share with them.
    /// </summary>
    internal int ParamBudget { get; init; } = 6000;

    internal EngineLocation? Location => _location.Value;

    /// <summary>Makes the kill on close job for one engine process. Tests swap it.</summary>
    internal Func<ProcessJob?> CreateJob { get; init; } = ProcessJob.Create;

    /// <summary>Called with the pid while the process is still suspended. Tests only.</summary>
    internal Action<int>? AfterCreate { get; init; }

    /// <summary>Called on the thread that prepares the call, before anything else. Tests only.</summary>
    internal Action? BeforeLaunch { get; init; }

    internal static string ListFolder => Path.Combine(Path.GetTempPath(), "Peel");

    /// <summary>
    /// Quitting: refuse new runs and end every engine process that is still
    /// going, with everything it started. Safe to call from any thread.
    /// </summary>
    internal void KillAll()
    {
        _closed = true;
        try
        {
            // Options calls and runs alike: every pending call ends now.
            _shutdown.Cancel();
        }
        catch (AggregateException error)
        {
            Log.Write("cancelling the engine calls failed", error);
        }
        foreach (var job in _live.Keys)
        {
            job.Terminate();
        }
    }

    /// <summary>
    /// Where the CLI is. Only real executables are ever run. The installer
    /// also writes a peel.cmd shim, and Peel deliberately ignores it: a .cmd
    /// has to go through cmd.exe, which parses its own command line after
    /// .NET has quoted the arguments, so a file called "a&amp;calc&amp;b.png" would
    /// run calc. Every candidate below is started directly, with the paths as
    /// real arguments, where no shell ever sees them.
    /// </summary>
    private static EngineLocation? Locate()
    {
        var overridden = Environment.GetEnvironmentVariable("PEEL_CLI");
        // Only an .exe: a .cmd or .bat would be run through cmd.exe.
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden)
            && string.Equals(Path.GetExtension(overridden), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineLocation(overridden, Array.Empty<string>(), "PEEL_CLI");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var venvExe = Path.Combine(local, "Peel", "venv", "Scripts", "peel.exe");
        if (File.Exists(venvExe))
        {
            return new EngineLocation(venvExe, Array.Empty<string>(), "venv peel.exe");
        }

        var venvPython = Path.Combine(local, "Peel", "venv", "Scripts", "python.exe");
        if (File.Exists(venvPython))
        {
            return new EngineLocation(venvPython, new[] { "-m", "peel_engine.cli" }, "venv python -m peel_engine.cli");
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "peel.exe");
                if (File.Exists(candidate))
                {
                    return new EngineLocation(candidate, Array.Empty<string>(), "peel.exe on PATH");
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not a reason to stop looking.
            }
        }

        return null;
    }

    internal async Task<OptionsResult> OptionsAsync(IReadOnlyList<string> files, CancellationToken token = default)
    {
        var (output, error, code) = await RunProcessAsync(
            new[] { "options" }, files, Array.Empty<string>(), OptionsTimeout, token).ConfigureAwait(false);
        if (code is null)
        {
            return OptionsResult.Failed(error, "shell_engine");
        }
        var parsed = Parse<OptionsResult>(output);
        if (parsed is null)
        {
            return OptionsResult.Failed(Describe(output, error, code.Value), "shell_engine");
        }
        return parsed;
    }

    /// <summary>
    /// Run one action. <paramref name="outDir"/> sends the outputs somewhere
    /// other than next to the source, which the editors use to rasterise a
    /// preview into a temporary folder without leaving a file by the user's.
    /// </summary>
    internal async Task<RunResult> RunAsync(
        string actionId,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken token = default,
        string? outDir = null)
    {
        var tail = new List<string>();
        string? paramsFile;
        try
        {
            paramsFile = ParamArgs(parameters, ParamBudget, tail);
        }
        catch (Exception error)
        {
            Log.Write("cannot write the parameters for the engine", error);
            return RunResult.Failed($"Peel could not pass the settings to the engine: {error.Message}", "shell_params");
        }
        if (outDir is not null)
        {
            tail.Add("--out-dir");
            tail.Add(outDir);
        }
        (string Output, string Error, int? ExitCode) call;
        try
        {
            call = await RunProcessAsync(
                new[] { "run", actionId }, files, tail, RunTimeout, token).ConfigureAwait(false);
        }
        finally
        {
            Discard(paramsFile);
        }
        var (output, problem, code) = call;
        if (code is null)
        {
            return RunResult.Failed(problem, "shell_engine");
        }
        var parsed = Parse<RunResult>(output);
        if (parsed is null)
        {
            return RunResult.Failed(Describe(output, problem, code.Value), "shell_engine");
        }
        return parsed;
    }

    /// <summary>
    /// The --param or --params-file arguments for one set of parameters.
    /// Returns the temporary file to delete afterwards, or null when the
    /// parameters went on the command line. A value starting with "@" always
    /// takes the file, because the engine would otherwise read it as a path.
    /// </summary>
    internal static string? ParamArgs(
        IReadOnlyDictionary<string, string>? parameters,
        int budget,
        List<string> tail,
        Action<string, string>? write = null)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }
        var length = 0;
        var magic = false;
        foreach (var pair in parameters)
        {
            length += pair.Key.Length + pair.Value.Length + 12;
            magic |= pair.Value.StartsWith('@');
        }
        if (!magic && length <= budget)
        {
            foreach (var pair in parameters)
            {
                tail.Add("--param");
                tail.Add($"{pair.Key}={pair.Value}");
            }
            return null;
        }

        var folder = ListFolder;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"params-{Guid.NewGuid():N}.json");
        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            foreach (var pair in parameters)
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
        }
        write ??= (path, contents) => File.WriteAllText(path, contents, new UTF8Encoding(false));
        try
        {
            write(file, Encoding.UTF8.GetString(json.ToArray()));
        }
        catch
        {
            // Same rule as the file list: a half written file is never kept.
            Discard(file);
            throw;
        }
        tail.Add("--params-file");
        tail.Add(file);
        return file;
    }

    private static void Discard(string? file)
    {
        if (file is null)
        {
            return;
        }
        try
        {
            File.Delete(file);
        }
        catch (Exception error)
        {
            Log.Write($"cannot delete the temporary file {file}", error);
        }
    }

    private static T? Parse<T>(string output) where T : class
    {
        var text = output.Trim();
        if (text.Length == 0 || text[0] != '{')
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(string output, string error, int exitCode)
    {
        var detail = error.Trim();
        if (detail.Length == 0)
        {
            detail = output.Trim();
        }
        if (detail.Length > 300)
        {
            detail = detail[..300] + "...";
        }
        return detail.Length == 0
            ? $"The Peel engine exited {exitCode} and said nothing."
            : $"The Peel engine exited {exitCode}: {detail}";
    }

    /// <summary>
    /// The arguments for one call: the paths inline when they fit, otherwise
    /// a list file. Returns the list file path (to delete) or null.
    /// </summary>
    internal static string? Arrange(
        EngineLocation location,
        IReadOnlyList<string> head,
        IReadOnlyList<string> files,
        IReadOnlyList<string> tail,
        int budget,
        List<string> arguments,
        Action<string, string>? write = null)
    {
        arguments.Clear();
        arguments.AddRange(location.Prefix);
        arguments.AddRange(head);
        var length = location.FileName.Length + 3;
        foreach (var argument in arguments.Concat(files).Concat(tail))
        {
            // Quotes, a separating space and a little for escaped backslashes.
            length += argument.Length + 4;
        }
        string? listFile = null;
        if (length > budget && files.Count > 0)
        {
            var folder = ListFolder;
            Directory.CreateDirectory(folder);
            listFile = Path.Combine(folder, $"files-{Guid.NewGuid():N}.txt");
            var text = new StringBuilder();
            foreach (var file in files)
            {
                text.Append(file).Append('\n');
            }
            write ??= (path, contents) => File.WriteAllText(path, contents, new UTF8Encoding(false));
            try
            {
                write(listFile, text.ToString());
            }
            catch
            {
                // A full disk can leave half a list behind: never keep it.
                try
                {
                    File.Delete(listFile);
                }
                catch (Exception error)
                {
                    Log.Write($"cannot delete the partial file list {listFile}", error);
                }
                throw;
            }
            arguments.Add("--files-from");
            arguments.Add(listFile);
        }
        else
        {
            arguments.AddRange(files);
        }
        arguments.AddRange(tail);
        return listFile;
    }

    /// <summary>
    /// Delete the temporary files a killed or crashed Peel left in %TEMP%\Peel
    /// (which is this user's own temp folder): the file lists it passes as
    /// --files-from and the parameter objects it passes as --params-file. Only
    /// ones older than <paramref name="olderThan"/>, so a file another Peel
    /// call is using right now is never touched. Returns how many were deleted.
    /// </summary>
    internal static int ScavengeListFiles(TimeSpan olderThan, string? folder = null)
    {
        folder ??= ListFolder;
        var deleted = 0;
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(folder, "files-*.txt")
                         .Concat(Directory.EnumerateFiles(folder, "params-*.json")))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (Exception error)
                {
                    Log.Write($"cannot delete the stale file list {file}", error);
                }
            }
        }
        catch (Exception error)
        {
            Log.Write("cannot sweep the stale file lists", error);
        }
        deleted += ScavengePreviews(olderThan, folder);
        return deleted;
    }

    /// <summary>
    /// The editors rasterise a preview into %TEMP%\Peel\preview-&lt;id&gt; and
    /// delete it when the window has the pixels. A killed Peel leaves one, so
    /// the stale ones go at the next start, like the file lists.
    /// </summary>
    private static int ScavengePreviews(TimeSpan olderThan, string folder)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var directory in Directory.EnumerateDirectories(folder, "preview-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                        deleted++;
                    }
                }
                catch (Exception error)
                {
                    Log.Write($"cannot delete the stale preview folder {directory}", error);
                }
            }
        }
        catch (Exception error)
        {
            Log.Write("cannot sweep the stale preview folders", error);
        }
        return deleted;
    }

    /// <summary>
    /// Returns stdout, stderr and the exit code. A null exit code means the
    /// process could not be started or had to be killed; stderr then holds the
    /// message to show. Always continues on the thread pool: nothing of the
    /// call runs on the caller's thread.
    /// </summary>
    private Task<(string Output, string Error, int? ExitCode)> RunProcessAsync(
        IReadOnlyList<string> head,
        IReadOnlyList<string> files,
        IReadOnlyList<string> tail,
        TimeSpan timeout,
        CancellationToken token) =>
        Task.Run(() => RunProcessOnPoolAsync(head, files, tail, timeout, token), CancellationToken.None);

    private async Task<(string Output, string Error, int? ExitCode)> RunProcessOnPoolAsync(
        IReadOnlyList<string> head,
        IReadOnlyList<string> files,
        IReadOnlyList<string> tail,
        TimeSpan timeout,
        CancellationToken token)
    {
        BeforeLaunch?.Invoke();
        var location = _location.Value;
        if (location is null)
        {
            return (string.Empty,
                "The Peel engine is not installed. Run install\\setup.ps1, then start Peel again.",
                null);
        }
        if (_closed)
        {
            return (string.Empty, "Peel is quitting.", null);
        }

        var arguments = new List<string>();
        string? listFile;
        try
        {
            listFile = Arrange(location, head, files, tail, CommandLineBudget, arguments);
        }
        catch (Exception error)
        {
            Log.Write("cannot write the file list for the engine", error);
            return (string.Empty, $"Peel could not pass {files.Count} files to the engine: {error.Message}", null);
        }
        var what = $"{head[0]} {(head.Count > 1 ? head[1] : string.Empty)}".Trim();
        try
        {
            return await RunListedAsync(location, arguments, what, timeout, token).ConfigureAwait(false);
        }
        finally
        {
            if (listFile is not null)
            {
                try
                {
                    File.Delete(listFile);
                }
                catch (Exception error)
                {
                    Log.Write($"cannot delete the file list {listFile}", error);
                }
            }
        }
    }

    private async Task<(string Output, string Error, int? ExitCode)> RunListedAsync(
        EngineLocation location,
        IReadOnlyList<string> arguments,
        string what,
        TimeSpan timeout,
        CancellationToken token)
    {
        var environment = new Dictionary<string, string>
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
        };

        var watch = Stopwatch.StartNew();
        var job = CreateJob();
        if (job is null)
        {
            // Fail closed: an engine Peel cannot end must not run.
            Log.Write($"engine {what} not run: no job object");
            return (string.Empty,
                "Peel could not set up the Windows job that ends the engine with Peel, so it did not run the engine. See the log.",
                null);
        }

        ContainedProcess process;
        try
        {
            process = ContainedProcess.Start(location.FileName, arguments, environment, job, AfterCreate, () =>
            {
                // Owned before it runs: from here KillAll reaches it.
                _live[job] = 0;
                if (_closed)
                {
                    throw new OperationCanceledException("Peel is quitting.");
                }
            });
        }
        catch (Exception error)
        {
            _live.TryRemove(job, out _);
            job.Dispose();
            if (error is OperationCanceledException)
            {
                return (string.Empty, "Peel is quitting.", null);
            }
            Log.Write($"cannot start the engine at {location.FileName}", error);
            return (string.Empty, $"Cannot start the Peel engine ({location.Description}): {error.Message}", null);
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            limit.CancelAfter(timeout);
            var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync(limit.Token);
            var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                // The engine has exited, but a backend it started may still hold
                // the pipes. The same deadline covers reading them to the end.
                await Task.WhenAll(output, errors).WaitAsync(limit.Token).ConfigureAwait(false);
                var stdout = await output.ConfigureAwait(false);
                var stderr = await errors.ConfigureAwait(false);
                Log.Write($"engine {what} exit={process.ExitCode} in {watch.ElapsedMilliseconds}ms");
                return (stdout, stderr, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // End the engine and everything it started, then close our
                // pipe ends so the readers return.
                job.Terminate();
                process.Kill();
                process.ClosePipes();
                await Observe(output, errors).ConfigureAwait(false);
                var reason = _closed ? "was stopped because Peel quit"
                    : token.IsCancellationRequested ? "was cancelled"
                    : $"took longer than {timeout.TotalSeconds:0} seconds";
                Log.Write($"engine {what} {reason}");
                return (string.Empty, $"The Peel engine {reason}.", null);
            }
            finally
            {
                _live.TryRemove(job, out _);
                // Kill on close: anything the engine left running goes too.
                job.Dispose();
            }
        }
    }

    /// <summary>
    /// Wait a moment for the readers to finish once their writers are dead,
    /// and observe whatever they ended with so nothing is left unobserved.
    /// </summary>
    private static async Task Observe(params Task[] readers)
    {
        try
        {
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled, broken pipe or still blocked: all expected here.
        }
        foreach (var reader in readers)
        {
            _ = reader.Exception;
        }
    }
}
