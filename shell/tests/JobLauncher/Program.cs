// Peel.JobLauncher <engine.exe>: start the engine through ContainedProcess,
// print its pid while it is still suspended, then never return from
// afterCreate. The test kills this process right there, which is what a Peel
// crash between CreateProcessW and the resume looks like to Windows. The
// engine inherits PEEL_FAKE_MODE and PEEL_FAKE_PIDS from this environment.
using System;
using System.Collections.Generic;
using System.Threading;
using Peel.Jobs;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Peel.JobLauncher <engine.exe>");
    return 2;
}
var job = ProcessJob.Create();
if (job is null)
{
    Console.Error.WriteLine("no job object");
    return 3;
}
ContainedProcess.Start(args[0], new[] { "run", "x.y" }, new Dictionary<string, string>(), job, pid =>
{
    Console.Out.WriteLine(pid);
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
});
return 1;
