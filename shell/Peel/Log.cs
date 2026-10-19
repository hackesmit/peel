// The log is Peel's only black box recorder, and the scripted end to end test
// asserts on its lines, so the format is stable: "HH:mm:ss.fff message".
//
// Writes are queued and flushed by a background thread. Nothing that runs on
// the low level hook thread may touch a file, and the state machine on that
// thread logs every transition, so Log.Write has to be a queue push and
// nothing more.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Peel;

internal static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly object FileLock = new();
    private static Thread? _worker;
    private static volatile bool _stopping;

    internal static string Path { get; private set; } = DefaultPath();

    private static string DefaultPath()
    {
        var overridden = Environment.GetEnvironmentVariable("PEEL_LOG");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(local, "Peel", "logs", "peel.log");
    }

    internal static void Start()
    {
        if (_worker is not null)
        {
            return;
        }
        Path = DefaultPath();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        }
        catch (Exception)
        {
            // An unwritable log must never stop the app; writes then fail quietly.
        }
        _worker = new Thread(Pump) { IsBackground = true, Name = "peel-log" };
        _worker.Start();
    }

    internal static void Write(string message)
    {
        Pending.Enqueue(string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} {message}"));
        Signal.Set();
    }

    internal static void Write(string message, Exception error) =>
        Write($"{message}: {error.GetType().Name}: {error.Message}");

    /// <summary>Block until the queue is on disk. Used before the app exits.</summary>
    internal static void Flush()
    {
        Signal.Set();
        for (var i = 0; i < 50 && !Pending.IsEmpty; i++)
        {
            Thread.Sleep(10);
        }
        FlushOnce();
    }

    internal static void Stop()
    {
        _stopping = true;
        Flush();
    }

    private static void Pump()
    {
        while (!_stopping)
        {
            Signal.WaitOne(200);
            FlushOnce();
        }
        FlushOnce();
    }

    private static void FlushOnce()
    {
        if (Pending.IsEmpty)
        {
            return;
        }
        var batch = new StringBuilder();
        while (Pending.TryDequeue(out var line))
        {
            batch.Append(line).Append("\r\n");
        }
        if (batch.Length == 0)
        {
            return;
        }
        lock (FileLock)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Rotate();
                    File.AppendAllText(Path, batch.ToString(), Encoding.UTF8);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }
            var previous = Path + ".1";
            File.Delete(previous);
            File.Move(Path, previous);
        }
        catch (Exception)
        {
            // Rotation is best effort.
        }
    }
}
