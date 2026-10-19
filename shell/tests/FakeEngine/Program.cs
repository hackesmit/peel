// A stand in for the `peel` CLI, used by the wheel rendering test.
//
// The registry on a developer machine holds whatever families have landed so
// far, which may be one conversion. The wheel has to be right with ten of them
// too, and with more than fit on one ring. This prints a canned `peel options`
// payload so a screenshot can show that, and refuses `peel run`, so nothing it
// is pointed at can ever write a file.
//
// Peel finds it through PEEL_CLI; the payload comes from PEEL_FAKE_OPTIONS.
// It is a test tool: it is not in Peel.sln and is never installed.
using System;
using System.IO;

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
