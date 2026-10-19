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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

    internal EngineClient() => _location = new Lazy<EngineLocation?>(Locate, isThreadSafe: true);

    internal TimeSpan OptionsTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    internal EngineLocation? Location => _location.Value;

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
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
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
        var arguments = new List<string> { "options" };
        arguments.AddRange(files);
        var (output, error, code) = await RunProcessAsync(arguments, OptionsTimeout, token).ConfigureAwait(false);
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

    internal async Task<RunResult> RunAsync(
        string actionId,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken token = default)
    {
        var arguments = new List<string> { "run", actionId };
        arguments.AddRange(files);
        if (parameters is not null)
        {
            foreach (var pair in parameters)
            {
                arguments.Add("--param");
                arguments.Add($"{pair.Key}={pair.Value}");
            }
        }
        var (output, error, code) = await RunProcessAsync(arguments, RunTimeout, token).ConfigureAwait(false);
        if (code is null)
        {
            return RunResult.Failed(error, "shell_engine");
        }
        var parsed = Parse<RunResult>(output);
        if (parsed is null)
        {
            return RunResult.Failed(Describe(output, error, code.Value), "shell_engine");
        }
        return parsed;
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
    /// Returns stdout, stderr and the exit code. A null exit code means the
    /// process could not be started or had to be killed; stderr then holds the
    /// message to show.
    /// </summary>
    private async Task<(string Output, string Error, int? ExitCode)> RunProcessAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken token)
    {
        var location = _location.Value;
        if (location is null)
        {
            return (string.Empty,
                "The Peel engine is not installed. Run install\\setup.ps1, then start Peel again.",
                null);
        }

        var start = new ProcessStartInfo
        {
            FileName = location.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in location.Prefix)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";

        var watch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            Log.Write($"cannot start the engine at {location.FileName}", error);
            return (string.Empty, $"Cannot start the Peel engine ({location.Description}): {error.Message}", null);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync();
        var error2 = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }
            var reason = token.IsCancellationRequested ? "was cancelled" : $"took longer than {timeout.TotalSeconds:0} seconds";
            Log.Write($"engine {string.Join(' ', arguments)} {reason}");
            return (string.Empty, $"The Peel engine {reason}.", null);
        }

        var stdout = await output.ConfigureAwait(false);
        var stderr = await error2.ConfigureAwait(false);
        Log.Write($"engine {arguments[0]} {(arguments.Count > 1 ? arguments[1] : string.Empty)} exit={process.ExitCode} in {watch.ElapsedMilliseconds}ms");
        return (stdout, stderr, process.ExitCode);
    }
}
