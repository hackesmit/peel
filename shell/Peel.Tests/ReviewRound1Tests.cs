// Reviewer checks for hq-plct.7, round 1. Each one states the behaviour the
// shell should have; a failure here is a finding, not a broken test.
//
// These run against real processes, so they need George:
//   * PEEL_REVIEW_FAKE: path to tests\FakeEngine\bin\Release\net8.0\FakeEngine.exe
//   * PEEL_REVIEW_OPTIONS: path to tests\fake-options.json
//   * the installed engine at %LOCALAPPDATA%\Peel\venv\Scripts\peel.exe
// A test whose prerequisite is missing returns early instead of failing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

[Collection("environment")]
public class ReviewRound1Tests
{
    private static string? Fake => Environment.GetEnvironmentVariable("PEEL_REVIEW_FAKE");

    private static string? Options => Environment.GetEnvironmentVariable("PEEL_REVIEW_OPTIONS");

    /// <summary>
    /// SPEC.md Batch: "a multi-file drag applies the action to every file".
    /// Seven hundred files of about sixty characters is one folder of photos,
    /// and it is over the 32767 character CreateProcess limit. The shell must
    /// still reach the engine (a list file, or chunked runs), not fail to
    /// start it.
    /// </summary>
    [Fact]
    public async Task ManyFilesStillReachTheEngine()
    {
        if (Fake is null || Options is null || !File.Exists(Fake))
        {
            return;
        }
        var previous = Environment.GetEnvironmentVariable("PEEL_CLI");
        Environment.SetEnvironmentVariable("PEEL_CLI", Fake);
        Environment.SetEnvironmentVariable("PEEL_FAKE_OPTIONS", Options);
        try
        {
            var files = Enumerable.Range(0, 700)
                .Select(i => $@"C:\Users\danie\Pictures\Vacaciones 2026 Ensenada\IMG_{i:00000}.JPG")
                .ToList();
            var total = files.Sum(f => f.Length + 3);
            Assert.True(total > 32767, $"fixture is only {total} characters");

            var result = await new EngineClient().OptionsAsync(files);

            Assert.True(result.Ok, $"expected the engine to answer for {files.Count} files, got: {result.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PEEL_CLI", previous);
        }
    }

    /// <summary>
    /// File names that a shell would interpret must reach the engine byte for
    /// byte. `peel options` echoes the paths it was given in "files".
    /// </summary>
    [Fact]
    public async Task HostileFileNamesRoundTripThroughTheRealEngine()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!File.Exists(Path.Combine(local, "Peel", "venv", "Scripts", "peel.exe")))
        {
            return;
        }
        var previous = Environment.GetEnvironmentVariable("PEEL_CLI");
        Environment.SetEnvironmentVariable("PEEL_CLI", null);
        var dir = Path.Combine(Path.GetTempPath(), "peel review & 100% ^caret");
        Directory.CreateDirectory(dir);
        try
        {
            var names = new[]
            {
                "a&calc&b.txt",
                "x ^& y.txt",
                "%PATH% %USERNAME%.txt",
                "-rf --help.txt",
                "comma, semi; (paren) [b] {c}.txt",
                "ñandú café 日本語 🍊.txt",
                "trailing space .txt",
                "'single' `tick` !bang.txt",
            };
            var paths = new List<string>();
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, "x");
                paths.Add(path);
            }
            // A folder too, the way Explorer offers it (no trailing slash).
            var folder = Path.Combine(dir, "sub folder & more");
            Directory.CreateDirectory(folder);
            paths.Add(folder);

            var result = await new EngineClient().OptionsAsync(paths);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(paths.Count, result.Files.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                Assert.Equal(
                    Path.GetFullPath(paths[i]).ToLowerInvariant(),
                    Path.GetFullPath(result.Files[i]).ToLowerInvariant());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PEEL_CLI", previous);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// An engine that prints a warning line before its JSON, or prints
    /// garbage, must come back as a failed result, never as an exception.
    /// </summary>
    [Fact]
    public async Task EngineNotFoundIsAMessageNotAnException()
    {
        var previous = Environment.GetEnvironmentVariable("PEEL_CLI");
        // A PEEL_CLI that does not exist falls through to the normal search;
        // point it at a real non engine executable instead.
        Environment.SetEnvironmentVariable("PEEL_CLI", Path.Combine(Environment.SystemDirectory, "whoami.exe"));
        try
        {
            var result = await new EngineClient().OptionsAsync(new[] { @"C:\nope.txt" });
            Assert.False(result.Ok);
            Assert.Equal("shell_engine", result.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PEEL_CLI", previous);
        }
    }
}

[CollectionDefinition("environment", DisableParallelization = true)]
public class EnvironmentCollection
{
}
