<#
Shared guard for every GUI driver that moves the real mouse or presses real
keys on George. Dot-source it:  . (Join-Path $PSScriptRoot 'gui-guard.ps1')

Why: on 2026-10-20 an aborted SendInput drag left an Explorer drag image (a
SysDragImage window, a file icon ghost) stuck on Daniel's screen. The cause was
his own mouse breaking the injected drag. So a driver must not start while
someone uses George, must stop when someone touches the mouse or keyboard, and
must always end its drag, even when a check throws or the run is killed. And a
run that never drove must leave George exactly as it found it.

The one way to inject: [PeelGuard]::Move / Place / Down / Up / Click / Key /
KeyEx / Tap / TapEx / Hotkey / Chord / CtrlT / CtrlShiftTab / Front. Drivers
declare no mouse_event, keybd_event or SetCursorPos of their own. Each call
  - refuses (PEEL_GUARD ...) before the drive has started or after it ended,
  - throws PEEL_ABORT user input once a person has touched George,
  - counts itself ([PeelGuard]::Injections) and appends a line to inject.log,
  - records what it pressed and has not let go (Owned) in guard-state.json.
Teardown input (Escape, release, cursor restore) is a separate, counted
path ([PeelGuard]::Cleanups) that only acts after this run drove, and only on
what the run itself pressed.

In a driver (interactive session):
  Start-GuiGuard -Dir $Dir [-RunDir $GuardDir] [-PauseInstalledFor $App]
                 -IdleSeconds 300 -IdleTimeout 1800 -Log { param($m) L $m }
                    the run's own folder is RunDir (a runner passes a fresh one;
                    without it one is made under Dir\runs). Waits for user idle
                    (GetLastInputInfo), logs the wait, then starts the real
                    input watcher (a low level mouse and keyboard hook on its
                    own thread: an event without LLMHF_INJECTED / LLKHF_INJECTED
                    is a person), opens the drive, pauses Daniel's installed
                    Peel when the app under test is another exe, and writes
                    driving.txt. A watchdog process tears down if this driver
                    dies before its finally.
  Assert-NoUserInput / Wait-Gui <ms>
                    throw "PEEL_ABORT user input: ..." once a person has
                    touched George; Wait-Gui sleeps in slices and checks.
  Get-GuiAbort $_   in the driver's catch: the abort reason ('user input',
                    'user busy') for a guard abort, or $null for a real failure.
  Stop-GuiGuard     first thing in the driver's finally: claim the run's
                    state, tear down what the run holds, close the drive, stop
                    the watcher and the watchdog. If it paused Daniel's Peel,
                    it stops the test copy (the -PauseInstalledFor exe) and
                    starts Daniel's Peel again.
  Restore-GuiCursor $pos
                    puts the cursor back, only when the run drove and no
                    person touched George (a busy run never moves it).

From session 0 (a runner over ssh):
  Invoke-GuiRun { ... }
                    takes C:\peel-dev\gui.lock (exclusive, names its pid),
                    runs the body, then starts the installed Peel again if it
                    ran before and is gone, and only then lets the lock go.
  Invoke-GuiTask    runs a driver as a unique Interactive task with a fresh
                    run folder; refuses without the lock.
  Invoke-InteractiveTeardown -RunDir <dir> -Report <json> [-Shot <png>]
                    runs gui-teardown.ps1 as a one shot Interactive task.

One owner cleans up: guard-state.json is claimed by renaming it (driver,
watchdog or runner), and a claimed state is taken over only when its owner
process is dead and it is not done. A run that never drove claims a state
with nothing to release, so no owner injects anything.
#>

if (-not ('PeelGuard' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class PeelGuard {
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint m; public IntPtr w; public IntPtr l; public uint t; public int x; public int y; public uint p; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc f, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    // Test seams, read once at load. They can only make George look busier,
    // never idler: PEEL_GUARD_FAKE_IDLE_MS caps the idle time,
    // PEEL_GUARD_FAKE_DOWN (vk list, "0x01,0xA0") reports keys as held, and
    // PEEL_GUARD_FAKE_REAL=1 reports a person as having touched George.
    static readonly long fakeIdle = ParseLong(Environment.GetEnvironmentVariable("PEEL_GUARD_FAKE_IDLE_MS"));
    static readonly HashSet<int> fakeDown = ParseVks(Environment.GetEnvironmentVariable("PEEL_GUARD_FAKE_DOWN"));
    static readonly bool fakeReal = Environment.GetEnvironmentVariable("PEEL_GUARD_FAKE_REAL") == "1";
    static long ParseLong(string s) { long v; return long.TryParse(s, out v) && v >= 0 ? v : -1; }
    static HashSet<int> ParseVks(string s) {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(s)) return set;
        foreach (var part in s.Split(',')) {
            var t = part.Trim(); int v;
            if (t.StartsWith("0x")) { if (int.TryParse(t.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v)) set.Add(v); }
            else if (int.TryParse(t, out v)) set.Add(v);
        }
        return set;
    }

    public static uint IdleMs() {
        var info = new LASTINPUTINFO(); info.cbSize = 8;
        GetLastInputInfo(ref info);
        uint idle = (uint)Environment.TickCount - info.dwTime;
        if (fakeIdle >= 0 && fakeIdle < idle) idle = (uint)fakeIdle;
        return idle;
    }

    /// <summary>GetLastInputInfo's tick: injected input moves it too, so an unchanged tick means nothing was injected.</summary>
    public static uint LastInputTick() { var info = new LASTINPUTINFO(); info.cbSize = 8; GetLastInputInfo(ref info); return info.dwTime; }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    /// <summary>The top level window under a screen point.</summary>
    public static long RootAt(int x, int y) { var p = new POINT(); p.X = x; p.Y = y; return GetAncestor(WindowFromPoint(p), 2).ToInt64(); }

    public static bool IsDown(int vk) { return fakeDown.Contains(vk) || (GetAsyncKeyState(vk) & 0x8000) != 0; }

    // Mouse buttons as virtual keys, and the mouse_event flags for each.
    public static readonly int[] Buttons = { 0x01, 0x02, 0x04 };
    static uint DownFlag(int vk) { return vk == 0x01 ? 0x0002u : vk == 0x02 ? 0x0008u : 0x0020u; }
    static uint UpFlag(int vk) { return vk == 0x01 ? 0x0004u : vk == 0x02 ? 0x0010u : 0x0040u; }
    // Left and right Shift, Ctrl, Alt and Windows.
    public static readonly int[] Modifiers = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
    static bool IsButton(int vk) { return vk == 0x01 || vk == 0x02 || vk == 0x04; }

    static string Class(IntPtr h) { var s = new StringBuilder(64); GetClassName(h, s, 64); return s.ToString(); }

    /// <summary>Visible top level SysDragImage windows: the drag image helper's ghost.</summary>
    public static List<IntPtr> DragImages() {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h) && Class(h) == "SysDragImage") found.Add(h); return true; }, IntPtr.Zero);
        return found;
    }

    public static int HideDragImages() {
        var images = DragImages();
        foreach (var h in images) { ShowWindow(h, 0); PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
        return images.Count;
    }

    // Show without taking the focus, on top of everything.
    public static void ShowShield(IntPtr h, int x, int y, int size) {
        SetWindowPos(h, new IntPtr(-1), x, y, size, size, 0x0010 | 0x0040);
        ShowWindow(h, 4);
    }

    // ---- the run's state on disk ----------------------------------------
    // guard-state.json in the run folder: who owns the run (pid), whether it
    // drives and ever drove, and the buttons and keys it pressed and has not
    // let go. The watchdog and the runner read it after the driver died.
    static string runDir = "";
    static string statePath = "";
    static volatile bool driving;
    static bool drove, done;
    static int injections, cleanups;
    static readonly HashSet<int> owned = new HashSet<int>();
    static readonly object stateGate = new object();

    public static bool Driving { get { return driving; } }
    public static bool Drove { get { return drove; } }
    public static int Injections { get { return injections; } }
    public static int Cleanups { get { return cleanups; } }
    public static string RunDir { get { return runDir; } }
    public static string StatePath { get { return statePath; } }
    public static int[] Owned() { lock (stateGate) { var a = new int[owned.Count]; owned.CopyTo(a); Array.Sort(a); return a; } }

    /// <summary>A driver's run begins: nothing driven, nothing owned.</summary>
    public static void Open(string dir) {
        lock (stateGate) {
            runDir = dir; statePath = Path.Combine(dir, "guard-state.json");
            driving = false; drove = false; done = false; injections = 0; cleanups = 0; owned.Clear();
        }
        Save();
    }

    /// <summary>A claimed state (gui-teardown.ps1, Stop-GuiGuard): act for its run.</summary>
    public static void Adopt(string dir, string path, bool didDrive, int[] keys, int count) {
        lock (stateGate) {
            runDir = dir; statePath = path; driving = false; drove = didDrive; done = false;
            injections = count; owned.Clear(); foreach (var k in keys) owned.Add(k);
        }
        Save();
    }

    /// <summary>The claim moved the state file: keep saving to the new name.</summary>
    public static void MovedTo(string path) { lock (stateGate) statePath = path; }

    public static void BeginDrive() { lock (stateGate) { driving = true; drove = true; } Save(); }
    public static void EndDrive() { driving = false; Save(); }
    public static void Finish() { lock (stateGate) { driving = false; done = true; } Save(); }

    // False when the state could not be written (the disk, a locked file).
    static bool Save() {
        string path, json;
        lock (stateGate) {
            path = statePath;
            if (path == "") return true;
            var keys = new List<string>();
            foreach (var k in Owned()) keys.Add(k.ToString());
            json = "{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id
                + ",\"driving\":" + (driving ? "true" : "false")
                + ",\"drove\":" + (drove ? "true" : "false")
                + ",\"done\":" + (done ? "true" : "false")
                + ",\"owned\":[" + string.Join(",", keys) + "]"
                + ",\"injections\":" + injections
                + ",\"cleanups\":" + cleanups + "}";
        }
        try {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return true;
        } catch (Exception) { return false; }
    }

    static void Log(string what) {
        if (runDir == "") return;
        try { File.AppendAllText(Path.Combine(runDir, "inject.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + what + "\r\n"); }
        catch (Exception) { }
    }

    static void Own(int vk, bool down) {
        bool changed;
        lock (stateGate) { changed = down ? owned.Add(vk) : owned.Remove(vk); }
        if (changed) Save();
    }

    // A press is on disk before it happens: a driver killed right after the
    // down event leaves a state that names it, so its teardown lets it go.
    // When the state cannot be written, nothing is pressed.
    static void OwnBefore(string what, int vk) {
        bool added;
        lock (stateGate) { added = owned.Add(vk); }
        if (!added || Save()) return;
        lock (stateGate) { owned.Remove(vk); }
        throw new InvalidOperationException("PEEL_GUARD refused " + what + ": guard-state.json could not be written, so the press would not be on record");
    }

    static void Refuse(string what) {
        if (!driving) throw new InvalidOperationException("PEEL_GUARD refused " + what + ": the run is not driving (before Start-GuiGuard opened the drive, or after its teardown)");
        if (Touched) throw new InvalidOperationException("PEEL_ABORT user input: " + FirstReal);
    }

    static void Count(string what) {
        Interlocked.Increment(ref injections);
        Log("drive " + what);
    }

    // Every driving injection passes here, and nothing else injects.
    static void Drive(string what) { Refuse(what); Count(what); }

    // A press or a release: a press is recorded first (OwnBefore), a release after.
    static void Press(string what, int vk, bool down) {
        Refuse(what);
        if (down) OwnBefore(what, vk);
        Count(what);
    }

    // Teardown input: only after this run drove. Counted apart.
    static bool Cleanup(string what) {
        if (!drove) return false;
        Interlocked.Increment(ref cleanups);
        Log("cleanup " + what);
        return true;
    }

    static void RawMove(int x, int y) {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int ax = (int)(((x - vx) * 65535.0) / (vw - 1)), ay = (int)(((y - vy) * 65535.0) / (vh - 1));
        mouse_event(0x0001 | 0x8000 | 0x4000, ax, ay, 0, UIntPtr.Zero);
    }
    static void RawKey(int vk, bool down, bool ext) { keybd_event((byte)vk, 0, (ext ? 1u : 0u) | (down ? 0u : 2u), UIntPtr.Zero); }
    static bool Extended(int vk) { return vk == 0xA3 || vk == 0xA5 || vk == 0x5B || vk == 0x5C; }

    // ---- the driving API ------------------------------------------------
    // SetCursorPos does not reach a low level hook. Absolute SendInput moves do.
    public static void Move(int x, int y) { Drive("move " + x + "," + y); RawMove(x, y); }
    public static void Place(int x, int y) { Drive("place " + x + "," + y); SetCursorPos(x, y); }
    public static void Button(int vk, bool down) {
        Press("button 0x" + vk.ToString("x2") + (down ? " down" : " up"), vk, down);
        mouse_event(down ? DownFlag(vk) : UpFlag(vk), 0, 0, 0, UIntPtr.Zero);
        if (!down) Own(vk, false);
    }
    public static void Down() { Button(0x01, true); }
    public static void Up() { Button(0x01, false); }
    public static void Click() { Down(); Thread.Sleep(60); Up(); }
    public static void Click(int x, int y) { Place(x, y); Thread.Sleep(120); Click(); }
    public static void KeyEx(byte vk, bool down, bool ext) {
        Press("key 0x" + vk.ToString("x2") + (down ? " down" : " up"), vk, down);
        RawKey(vk, down, ext);
        if (!down) Own(vk, false);
    }
    public static void Key(byte vk, bool down) { KeyEx(vk, down, false); }
    public static void TapEx(byte vk, bool ext) { KeyEx(vk, true, ext); Thread.Sleep(45); KeyEx(vk, false, ext); }
    public static void Tap(byte vk) { KeyEx(vk, true, false); Thread.Sleep(60); KeyEx(vk, false, false); }
    /// <summary>Ctrl+Alt+key, pressed as a chord and released cleanly.</summary>
    public static void Chord(byte key) {
        Key(0x11, true);  Thread.Sleep(40);
        Key(0x12, true);  Thread.Sleep(40);
        Key(key, true);   Thread.Sleep(60);
        Key(key, false);  Thread.Sleep(40);
        Key(0x12, false); Thread.Sleep(40);
        Key(0x11, false);
    }
    /// <summary>Ctrl+Alt+Enter, Peel's default global hotkey.</summary>
    public static void Hotkey() { Chord(0x0D); }
    public static void CtrlT() { Key(0x11, true); Thread.Sleep(40); TapEx(0x54, false); Thread.Sleep(40); Key(0x11, false); }
    public static void CtrlShiftTab() { Key(0x11, true); Key(0x10, true); Thread.Sleep(40); TapEx(0x09, false); Thread.Sleep(40); Key(0x10, false); Key(0x11, false); }
    /// <summary>Bring a window to the front: a tapped Alt lets SetForegroundWindow work.</summary>
    public static bool Front(long handle) {
        var w = new IntPtr(handle);
        ShowWindow(w, 9);
        Key(0x12, true); SetForegroundWindow(w); Key(0x12, false);
        Thread.Sleep(300);
        return GetForegroundWindow() == w;
    }

    // ---- the teardown API (only after the run drove) ---------------------
    public static bool CleanupEscape() {
        if (!Cleanup("escape")) return false;
        RawKey(0x1B, true, false); Thread.Sleep(60); RawKey(0x1B, false, false);
        return true;
    }
    public static bool CleanupNudge() {
        if (!Cleanup("nudge")) return false;
        mouse_event(0x0001, 1, 1, 0, UIntPtr.Zero); Thread.Sleep(40);
        mouse_event(0x0001, -1, -1, 0, UIntPtr.Zero); Thread.Sleep(40);
        return true;
    }
    /// <summary>Let go of one thing the run pressed. Anything else is refused.</summary>
    public static bool Release(int vk) {
        lock (stateGate) { if (!owned.Contains(vk)) return false; }
        if (!Cleanup("release 0x" + vk.ToString("x2"))) return false;
        if (IsButton(vk)) mouse_event(UpFlag(vk), 0, 0, 0, UIntPtr.Zero);
        else RawKey(vk, false, Extended(vk));
        Own(vk, false);
        return true;
    }
    /// <summary>Escape for a share sheet in the run's own cleanup: not once a person touched George.</summary>
    public static bool CleanupTap(byte vk) {
        if (Touched || !Cleanup("tap 0x" + vk.ToString("x2"))) return false;
        RawKey(vk, true, false); Thread.Sleep(60); RawKey(vk, false, false);
        return true;
    }
    /// <summary>Put the cursor back, only when the run drove and no person moved it since.</summary>
    public static bool RestoreCursor(int x, int y) {
        if (Touched || !Cleanup("restore cursor " + x + "," + y)) return false;
        SetCursorPos(x, y);
        return true;
    }

    // ---- the real input watcher -------------------------------------------
    static Thread watcher;
    static uint watcherId;
    static IntPtr mouseHook, keyHook;
    static HookProc mouseProc = MouseProc, keyProc = KeyProc;
    static int real;
    static string firstReal = "";
    static readonly object gate = new object();

    public static int RealInput { get { return fakeReal && real == 0 ? 1 : real; } }
    /// <summary>A person has touched George since the watcher started (or the seam says so).</summary>
    public static bool Touched { get { return real > 0 || fakeReal; } }
    public static string FirstReal { get { lock (gate) return firstReal != "" ? firstReal : fakeReal ? "PEEL_GUARD_FAKE_REAL" : ""; } }
    public static bool Watching { get { return watcher != null; } }

    static void Real(string what) {
        lock (gate) { if (real == 0) firstReal = what + " at " + DateTime.Now.ToString("HH:mm:ss.fff"); }
        Interlocked.Increment(ref real);
    }

    static IntPtr MouseProc(int code, IntPtr w, IntPtr l) {
        if (code >= 0 && (Marshal.ReadInt32(l, 12) & 1) == 0) Real("mouse message 0x" + ((int)w).ToString("x"));
        return CallNextHookEx(mouseHook, code, w, l);
    }

    static IntPtr KeyProc(int code, IntPtr w, IntPtr l) {
        if (code >= 0 && (Marshal.ReadInt32(l, 8) & 0x10) == 0) Real("key 0x" + Marshal.ReadInt32(l, 0).ToString("x"));
        return CallNextHookEx(keyHook, code, w, l);
    }

    public static bool StartWatch() {
        if (watcher != null) return true;
        real = 0; lock (gate) { firstReal = ""; }
        var ready = new ManualResetEvent(false);
        bool ok = false;
        var t = new Thread(() => {
            watcherId = GetCurrentThreadId();
            var mod = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(14, mouseProc, mod, 0);
            keyHook = SetWindowsHookEx(13, keyProc, mod, 0);
            ok = mouseHook != IntPtr.Zero && keyHook != IntPtr.Zero;
            ready.Set();
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        });
        t.IsBackground = true;
        t.Start();
        ready.WaitOne(5000);
        watcher = t;
        return ok;
    }

    public static void StopWatch() {
        if (watcher == null) return;
        PostThreadMessage(watcherId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        watcher.Join(2000);
        watcher = null;
    }
}
"@
}
[void][PeelGuard]::SetProcessDPIAware()

$script:GuiGuardHere = $PSScriptRoot
$script:GuiGuardLog = { param($m) }
$script:GuiGuardWatchdog = $null
$script:GuiGuardPaused = $null
$script:GuiGuardPausedFor = $null
# Arguments for the restart of Daniel's Peel: none. test-gui-guard.ps1 points
# GuiGuardPaused at a probe and passes it what to write.
$script:GuiGuardRestartArgs = ''
$script:GuiLock = $null
$script:GuiLockPath = 'C:\peel-dev\gui.lock'
$script:InstalledPeel = Join-Path $env:LOCALAPPDATA 'Peel\app\Peel.exe'

function Write-GuiGuardLog($message) { & $script:GuiGuardLog $message }

# Waits until nobody has touched George for $Seconds (0 skips the wait, for a
# driver nested in another that already waited), or throws a 'user busy' abort
# after $Timeout seconds.
function Wait-UserIdle([int]$Seconds, [int]$Timeout) {
    if ($Seconds -le 0) { Write-GuiGuardLog 'idle wait skipped (-IdleSeconds 0)'; return }
    $started = Get-Date
    $deadline = $started.AddSeconds($Timeout)
    Write-GuiGuardLog ("waiting for $Seconds s of user idle (idle now {0:0} s)" -f ([PeelGuard]::IdleMs() / 1000))
    $beat = $started
    while ([PeelGuard]::IdleMs() -lt ($Seconds * 1000)) {
        if ((Get-Date) -gt $deadline) {
            throw "PEEL_ABORT user busy: no $Seconds s of user idle within $Timeout s, so nothing was driven"
        }
        if (((Get-Date) - $beat).TotalSeconds -ge 60) {
            $beat = Get-Date
            Write-GuiGuardLog ("still waiting: idle {0:0} s" -f ([PeelGuard]::IdleMs() / 1000))
        }
        Start-Sleep -Milliseconds ([Math]::Min(5000, [Math]::Max(200, $Timeout * 250)))
    }
    Write-GuiGuardLog ("George idle for {0:0} s after waiting {1:0} s" -f ([PeelGuard]::IdleMs() / 1000), ((Get-Date) - $started).TotalSeconds)
}

# The installed Peel holds the single instance mutex, so a test of another
# Peel.exe pauses it once the drive starts (never during the idle wait).
# Starts an exe with this process's environment minus every PEEL_* variable.
# A driver sets PEEL_CLI (a fake engine), PEEL_EXIT_AFTER and PEEL_LOG for the
# copy under test; Daniel's Peel, started again from the driver, must not get them.
function Start-CleanProcess([string]$FilePath, [string]$Arguments = '') {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $info.WorkingDirectory = Split-Path -Parent $FilePath
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -like 'PEEL_*') { $info.EnvironmentVariables.Remove($name) }
    }
    return [System.Diagnostics.Process]::Start($info)
}

function Get-InstalledPeel {
    @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $script:InstalledPeel })
}

function Start-GuiGuard {
    param(
        [Parameter(Mandatory)] [string]$Dir,
        [string]$RunDir = '',
        [string]$PauseInstalledFor = '',
        [int]$IdleSeconds = 300,
        [int]$IdleTimeout = 1800,
        [scriptblock]$Log = { param($m) }
    )
    $script:GuiGuardLog = $Log
    if (-not $RunDir) { $RunDir = Join-Path $Dir ('runs\{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), $PID) }
    New-Item -ItemType Directory -Force $RunDir | Out-Null
    [PeelGuard]::Open($RunDir)
    Write-GuiGuardLog "run folder $RunDir"
    # The runner kills this pid if it loses track of the task (Invoke-GuiTask).
    Set-Content (Join-Path $RunDir 'driver.pid') $PID
    # A teardown that a dead driver never got to run: the watchdog does it.
    $teardown = Join-Path $script:GuiGuardHere 'gui-teardown.ps1'
    $script:GuiGuardWatchdog = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$teardown`"", '-WaitPid', $PID,
        '-RunDir', "`"$RunDir`"", '-Report', "`"$(Join-Path $RunDir 'teardown-watchdog.json')`"")
    Write-GuiGuardLog "watchdog pid $($script:GuiGuardWatchdog.Id) waits on driver pid $PID"
    Wait-UserIdle $IdleSeconds $IdleTimeout
    if (-not [PeelGuard]::StartWatch()) { throw 'the real input watcher could not install its hooks' }
    Write-GuiGuardLog 'real input watcher on: any input without the injected flag aborts the drive'
    [PeelGuard]::BeginDrive()
    if ($PauseInstalledFor -and $PauseInstalledFor -ine $script:InstalledPeel) {
        $installed = Get-InstalledPeel
        if ($installed.Count -gt 0) {
            # The runner reads this if the driver dies with Daniel's Peel paused.
            Set-Content (Join-Path $RunDir 'installed-paused.txt') $script:InstalledPeel
            $script:GuiGuardPaused = $script:InstalledPeel
            $script:GuiGuardPausedFor = $PauseInstalledFor
            foreach ($process in $installed) {
                Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
                Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
            }
            Write-GuiGuardLog "paused the installed Peel ($($installed.Count) process)"
        }
    }
    Set-Content (Join-Path $RunDir 'driving.txt') (Get-Date).ToString('o')
}

function Assert-NoUserInput {
    if ([PeelGuard]::Watching -and [PeelGuard]::RealInput -gt 0) {
        throw "PEEL_ABORT user input: $([PeelGuard]::FirstReal)"
    }
}

# A wait that stops as soon as a person touches George.
function Wait-Gui([int]$Milliseconds) {
    $until = (Get-Date).AddMilliseconds($Milliseconds)
    do {
        Assert-NoUserInput
        Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min(100, ($until - (Get-Date)).TotalMilliseconds)))
    } while ((Get-Date) -lt $until)
    Assert-NoUserInput
}

# A guard abort can arrive wrapped ('Exception calling "Move" ...: "PEEL_ABORT ...').
function Get-GuiAbort($errorRecord) {
    if ("$errorRecord" -match 'PEEL_ABORT (user input|user busy)') { return $Matches[1] }
    return $null
}

# Takes the run's state for one owner ($By: driver, watchdog or runner): the
# first state that is not done and whose owner process is gone (or is us) is
# renamed to guard-state.<By>.json. $null when no state needs an owner.
function Request-GuiState([string]$RunDir, [string]$By) {
    if (-not $RunDir -or -not (Test-Path $RunDir)) { return $null }
    $mine = Join-Path $RunDir "guard-state.$By.json"
    foreach ($name in 'guard-state.json', 'guard-state.driver.json', 'guard-state.watchdog.json', 'guard-state.runner.json') {
        $path = Join-Path $RunDir $name
        if (-not (Test-Path $path)) { continue }
        try { $state = Get-Content $path -Raw | ConvertFrom-Json } catch { continue }
        if ($state.done) { continue }
        if ($state.pid -ne $PID -and (Get-Process -Id $state.pid -ErrorAction SilentlyContinue)) { continue }
        if ($path -ne $mine) {
            try { Move-Item -LiteralPath $path -Destination $mine -ErrorAction Stop } catch { continue }
        }
        return [pscustomobject]@{ Path = $mine; State = $state }
    }
    return $null
}

# Ends the drag the run started and lets go of what the run pressed, nothing
# else: no Escape, no release and no cursor move when the run never drove or
# holds nothing. A drag image is hidden only when the run held a button, or
# drove and no person touched George (then it can only be the run's own).
function Invoke-GuiTeardown {
    $done = [System.Collections.ArrayList]::new()
    $seenPerson = [PeelGuard]::RealInput -gt 0
    $before = [PeelGuard]::Cleanups
    $imagesFound = ([PeelGuard]::DragImages()).Count
    $owned = @([PeelGuard]::Owned())
    $buttons = @($owned | Where-Object { [PeelGuard]::Buttons -contains $_ })
    $keys = @($owned | Where-Object { [PeelGuard]::Buttons -notcontains $_ })

    if (-not [PeelGuard]::Drove) {
        $null = $done.Add('nothing driven, nothing touched')
    }
    if ($buttons.Count -gt 0) {
        # Escape first: the drag loop of the window that started the drag
        # takes it as a cancel. Then the shield, so that a drag that missed
        # the Escape drops on a window with no drop target: no file moves.
        if ([PeelGuard]::CleanupEscape()) { $null = $done.Add('escape') }
        Start-Sleep -Milliseconds 250
        $shield = $null
        try {
            Add-Type -AssemblyName System.Windows.Forms
            $at = [System.Windows.Forms.Cursor]::Position
            $shield = New-Object System.Windows.Forms.Form
            $shield.FormBorderStyle = 'None'
            $shield.ShowInTaskbar = $false
            $shield.Opacity = 0.02
            $shield.AllowDrop = $false
            [PeelGuard]::ShowShield($shield.Handle, $at.X - 40, $at.Y - 40, 80)
            [System.Windows.Forms.Application]::DoEvents()
            [void][PeelGuard]::CleanupNudge()
            [System.Windows.Forms.Application]::DoEvents()
            $null = $done.Add('shield')
        }
        catch { $null = $done.Add("no shield ($($_.Exception.Message))") }
        $released = @($buttons | Where-Object { [PeelGuard]::Release($_) })
        $null = $done.Add('released buttons ' + (($released | ForEach-Object { '0x{0:x2}' -f $_ }) -join ','))
        Start-Sleep -Milliseconds 300
        if ($shield) {
            [System.Windows.Forms.Application]::DoEvents()
            $shield.Close(); $shield.Dispose()
        }
    }
    if ($keys.Count -gt 0) {
        $released = @($keys | Where-Object { [PeelGuard]::Release($_) })
        $null = $done.Add('released keys ' + (($released | ForEach-Object { '0x{0:x2}' -f $_ }) -join ','))
    }
    if ($imagesFound -gt 0 -and ($buttons.Count -gt 0 -or ([PeelGuard]::Drove -and -not $seenPerson))) {
        Start-Sleep -Milliseconds 200
        $hidden = [PeelGuard]::HideDragImages()
        if ($hidden -gt 0) { $null = $done.Add("hid $hidden drag image(s)"); Start-Sleep -Milliseconds 300 }
    }

    $imagesLeft = ([PeelGuard]::DragImages()).Count
    $left = @([PeelGuard]::Owned() | ForEach-Object { '0x{0:x2}' -f $_ })
    $stillDown = @([PeelGuard]::Buttons + [PeelGuard]::Modifiers | Where-Object { [PeelGuard]::IsDown($_) } |
        ForEach-Object { '0x{0:x2}' -f $_ })
    $actions = if ($done.Count -gt 0) { $done -join ', ' } else { 'nothing held' }
    $cleanups = [PeelGuard]::Cleanups - $before
    return [pscustomobject]@{
        Actions          = $actions
        Drove            = [PeelGuard]::Drove
        Injections       = [PeelGuard]::Injections
        Cleanups         = $cleanups
        DragImagesFound  = $imagesFound
        DragImagesLeft   = $imagesLeft
        OwnedLeft        = ($left -join ',')
        StillDown        = ($stillDown -join ',')
        PersonSeen       = [bool]$seenPerson
        Clean            = ($left.Count -eq 0 -and ($imagesLeft -eq 0 -or -not [PeelGuard]::Drove))
        Text             = "teardown: $actions; drove $([PeelGuard]::Drove), injections $([PeelGuard]::Injections), cleanup inputs $cleanups; drag images found $imagesFound, left $imagesLeft; still held by the run: $(if ($left) { $left -join ',' } else { 'none' }); down on George: $(if ($stillDown) { $stillDown -join ',' } else { 'none' })"
    }
}

function Stop-GuiGuard {
    if (-not [PeelGuard]::RunDir) {
        return [pscustomobject]@{ Text = 'teardown: the guard never started, nothing driven'; Drove = $false; Injections = 0; Cleanups = 0; Clean = $true }
    }
    [PeelGuard]::EndDrive()
    $claim = Request-GuiState ([PeelGuard]::RunDir) 'driver'
    if ($claim) {
        [PeelGuard]::MovedTo($claim.Path)
        $result = Invoke-GuiTeardown
        [PeelGuard]::Finish()
    }
    else {
        $result = [pscustomobject]@{ Text = 'teardown: another owner holds this run''s state'; Drove = [PeelGuard]::Drove; Injections = [PeelGuard]::Injections; Cleanups = 0; Clean = $true }
    }
    [PeelGuard]::StopWatch()
    if ($script:GuiGuardWatchdog) {
        Stop-Process -Id $script:GuiGuardWatchdog.Id -Force -ErrorAction SilentlyContinue
        $script:GuiGuardWatchdog = $null
    }
    if ($script:GuiGuardPaused) {
        # The test copy first: a Peel started while it runs only asks it to
        # open its settings (the single instance mutex) and exits.
        foreach ($process in @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $script:GuiGuardPausedFor })) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        if ((Get-InstalledPeel).Count -eq 0) {
            $restarted = Start-CleanProcess -FilePath $script:GuiGuardPaused -Arguments $script:GuiGuardRestartArgs
            $result.Text += "; restarted the installed Peel (pid $($restarted.Id), no PEEL_* variables)"
        }
        Remove-Item (Join-Path ([PeelGuard]::RunDir) 'installed-paused.txt') -ErrorAction SilentlyContinue
        $script:GuiGuardPaused = $null
    }
    return $result
}

# UI Automation reports an item's rectangle even when another window covers
# it, so a grab can land on whatever is on top (Daniel's browser, a console).
# Brings the item's own top level window to the front when it is not the one
# under ($x, $y). Returns $true when the point is on the item's window.
function Confirm-GrabPoint($item, [int]$x, [int]$y) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $top = $item
    for ($i = 0; $i -lt 50; $i++) {
        $parent = $walker.GetParent($top)
        if (-not $parent -or $parent -eq $root) { break }
        $top = $parent
    }
    $window = [long]$top.Current.NativeWindowHandle
    if (-not $window) { Write-GuiGuardLog "grab check: no window handle for the item"; return $true }
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $under = [PeelGuard]::RootAt($x, $y)
        if ($under -eq $window) { return $true }
        Write-GuiGuardLog ("grab check: window {0} is over the item's window {1} at {2},{3}; bringing it to the front" -f $under, $window, $x, $y)
        [void][PeelGuard]::Front($window)
        Wait-Gui 400
    }
    return ([PeelGuard]::RootAt($x, $y) -eq $window)
}

function Restore-GuiCursor($position) {
    if ($position) { [void][PeelGuard]::RestoreCursor([int]$position.X, [int]$position.Y) }
}

# ---- session 0 side: the runner ---------------------------------------------

# Takes C:\peel-dev\gui.lock for this runner: created exclusively, held open
# (others can read it, nobody can delete it), and deleted by Exit-GuiLock. Its
# text names the holder's pid and, one RUN line each, the run folders it gave
# a driver (Add-GuiLockRun). A lock left by a killed runner whose pid is gone
# is taken over, but only after Reset-DeadGuiRun has stopped that runner's
# driver and torn its run down. Waits up to $Timeout seconds for a live
# holder, then throws (nothing was driven).
function Enter-GuiLock([int]$Timeout = 1800) {
    if ($script:GuiLock) { return }
    $deadline = (Get-Date).AddSeconds($Timeout)
    $beat = Get-Date
    while ($true) {
        try {
            $stream = [System.IO.FileStream]::new($script:GuiLockPath, [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            $bytes = [System.Text.Encoding]::UTF8.GetBytes(("LOCKED by {0} pid {1} at {2:o}" -f $MyInvocation.PSCommandPath, $PID, (Get-Date)))
            $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
            $script:GuiLock = $stream
            Write-Host "gui.lock taken (pid $PID)"
            return
        }
        catch [System.IO.IOException] {
            $holder = try { Get-Content $script:GuiLockPath -Raw -ErrorAction Stop } catch { '' }
            if ($holder -match 'LOCKED by .* pid (\d+) at' -and -not (Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue)) {
                # A killed runner's lock: its pid is gone, and nobody holds the file open.
                $deadPid = $Matches[1]
                try {
                    Reset-DeadGuiRun $holder
                    Remove-Item -LiteralPath $script:GuiLockPath -ErrorAction Stop
                    Write-Host "gui.lock of dead pid $deadPid removed"
                    continue
                } catch { Write-Host "gui.lock of dead pid ${deadPid}: not taken over ($($_.Exception.Message))" }
            }
            if ((Get-Date) -gt $deadline) {
                throw "PEEL_ABORT user busy: $($script:GuiLockPath) is held ($(if ($holder) { $holder.Trim() } else { 'unknown holder' })) for over $Timeout s, so nothing was driven"
            }
            if (((Get-Date) - $beat).TotalSeconds -ge 60) { $beat = Get-Date; Write-Host "waiting for gui.lock: $holder" }
            Start-Sleep -Seconds ([Math]::Min(5, [Math]::Max(1, $Timeout)))
        }
    }
}

# Notes a run folder in the held lock, so a runner that takes the lock over
# after this one was killed can find the driver it left (Reset-DeadGuiRun).
function Add-GuiLockRun([string]$RunDir) {
    if (-not $script:GuiLock) { return }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("`r`nRUN $RunDir")
    $script:GuiLock.Write($bytes, 0, $bytes.Length); $script:GuiLock.Flush()
}

# Before a dead runner's lock is taken over: for each run folder it names,
# kill a driver still alive (a powershell started after the lock was taken,
# so a reused pid is never hit), give its watchdog time to report, then the
# runner's teardown (Complete-GuiRun: it acts only on a state nobody
# finished), and start Daniel's Peel again if that run paused it.
function Reset-DeadGuiRun([string]$Holder) {
    $since = [datetime]::MinValue
    if ($Holder -match ' at (\S+)') { try { $since = [datetime]::Parse($Matches[1]) } catch { } }
    foreach ($line in @($Holder -split "`r?`n" | Where-Object { $_ -match '^RUN (.+)$' })) {
        $runDir = ($line -replace '^RUN ', '').Trim()
        if (-not (Test-Path $runDir)) { continue }
        $pidPath = Join-Path $runDir 'driver.pid'
        $killed = $false
        if (Test-Path $pidPath) {
            $driver = Get-Process -Id ([int](Get-Content $pidPath -Raw)) -ErrorAction SilentlyContinue
            if ($driver -and $driver.ProcessName -eq 'powershell' -and $driver.StartTime -ge $since) {
                Stop-Process -Id $driver.Id -Force -ErrorAction SilentlyContinue
                Wait-Process -Id $driver.Id -Timeout 10 -ErrorAction SilentlyContinue
                $killed = $true
                Write-Host "dead runner's driver pid $($driver.Id) killed ($runDir)"
            }
        }
        if ($killed) {
            $wait = (Get-Date).AddSeconds(15)
            while (-not (Test-Path (Join-Path $runDir 'teardown-watchdog.json')) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 500 }
        }
        [void](Complete-GuiRun -RunDir $runDir -DriverDied $true)
        $paused = Join-Path $runDir 'installed-paused.txt'
        if ((Test-Path $paused) -and @(Get-InstalledPeel | Where-Object { $_.SessionId -ne 0 }).Count -eq 0) {
            Start-InstalledPeelInteractive
            Write-Host "dead runner's run had paused the installed Peel: started it again"
        }
        Remove-Item $paused -ErrorAction SilentlyContinue
    }
}

function Exit-GuiLock {
    if (-not $script:GuiLock) { return }
    $script:GuiLock.Dispose(); $script:GuiLock = $null
    Remove-Item -LiteralPath $script:GuiLockPath -ErrorAction SilentlyContinue
    Write-Host 'gui.lock released'
}

# Starts the installed Peel in the desktop session from session 0.
function Start-InstalledPeelInteractive {
    $task = 'PeelRestore-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument ('-NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath ''{0}''"' -f $script:InstalledPeel)
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds(20)
        do { Start-Sleep -Milliseconds 500 } while (@(Get-InstalledPeel | Where-Object { $_.SessionId -ne 0 }).Count -eq 0 -and (Get-Date) -lt $deadline)
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
}

# The runner's outer frame: the lock around everything, and Daniel's Peel
# running again at the end if it ran before, both inside the lock.
function Invoke-GuiRun {
    param([Parameter(Mandatory)] [scriptblock]$Body, [int]$LockTimeout = 1800)
    Enter-GuiLock $LockTimeout
    $wasRunning = @(Get-InstalledPeel | Where-Object { $_.SessionId -ne 0 }).Count -gt 0
    try { & $Body }
    finally {
        try {
            if ($wasRunning -and @(Get-InstalledPeel | Where-Object { $_.SessionId -ne 0 }).Count -eq 0) {
                Start-InstalledPeelInteractive
                Write-Host ("installed Peel restarted: {0} running" -f @(Get-InstalledPeel | Where-Object { $_.SessionId -ne 0 }).Count)
            }
        }
        finally { Exit-GuiLock }
    }
}

# Stops the test copies of Peel a driver left (the exe under test), never
# Daniel's installed one unless that is the exe under test.
function Stop-TestPeel([string]$App) {
    if (-not $App) { return }
    Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $App } | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        Write-Host "stopped the test Peel pid $($_.Id) ($App)"
    }
}

# The whole runner side in one call: gui.lock, the driver's task, the test
# copies of -App stopped if the driver left any, and Daniel's Peel back, all
# inside the lock. Returns the run (Invoke-GuiTask), or throws PEEL_ABORT user
# busy when the lock stayed held by another run (nothing was scheduled).
function Invoke-GuiDriver {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Arguments,
        [Parameter(Mandatory)] [string]$Dir,
        [Parameter(Mandatory)] [string]$ResultPath,
        [string]$App = '',
        [int]$TimeoutSeconds = 900,
        [int]$IdleTimeout = 1800,
        [ValidateSet('Limited', 'Highest')] [string]$RunLevel = 'Limited',
        [scriptblock]$OnDriving = $null
    )
    $script:GuiDriverRun = $null
    Invoke-GuiRun -LockTimeout $IdleTimeout {
        try {
            $script:GuiDriverRun = Invoke-GuiTask -Name $Name -Arguments $Arguments -Dir $Dir -ResultPath $ResultPath `
                -TimeoutSeconds $TimeoutSeconds -IdleTimeout $IdleTimeout -RunLevel $RunLevel -OnDriving $OnDriving
        }
        finally { Stop-TestPeel $App }
    }
    return $script:GuiDriverRun
}

# Session 0 side: run gui-teardown.ps1 in the logged on user's desktop.
function Invoke-InteractiveTeardown {
    param([Parameter(Mandatory)] [string]$RunDir, [Parameter(Mandatory)] [string]$Report, [string]$Shot = '')
    $teardown = Join-Path $script:GuiGuardHere 'gui-teardown.ps1'
    Remove-Item $Report -ErrorAction SilentlyContinue
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -RunDir "{1}" -Report "{2}"' -f $teardown, $RunDir, $Report
    if ($Shot) { $arguments += ' -Shot "{0}"' -f $Shot }
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    $task = 'PeelGuiTeardown-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Register-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments) `
        -Principal $principal -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds(60)
        do { Start-Sleep -Seconds 1 } while (-not (Test-Path $Report) -and (Get-Date) -lt $deadline)
        Start-Sleep -Milliseconds 300
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path $Report)) { return $null }
    return (Get-Content $Report -Raw | ConvertFrom-Json)
}

# Runner side (session 0): run a driver as a scheduled task in the desktop
# session and make sure its drag ends whatever happens. Needs gui.lock
# (Invoke-GuiRun). The task name is unique per run, so two runners never stop
# or delete each other's task, and the run folder is fresh, so no driver.pid
# or driving.txt of an older run is ever read: the driver gets it as
# -GuardDir. The drive budget (-TimeoutSeconds) starts when the driver writes
# driving.txt, after its idle wait (bounded by -IdleTimeout). -OnDriving runs
# once at that moment. A task that times out, or vanishes, has its driver
# killed by pid; then Complete-GuiRun tears down. Returns
# @{ TimedOut; Died; Teardown; RunDir }.
function Invoke-GuiTask {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Arguments,
        [Parameter(Mandatory)] [string]$Dir,
        [Parameter(Mandatory)] [string]$ResultPath,
        [int]$TimeoutSeconds = 900,
        [int]$IdleTimeout = 1800,
        [ValidateSet('Limited', 'Highest')] [string]$RunLevel = 'Limited',
        [scriptblock]$OnDriving = $null
    )
    if (-not $script:GuiLock) { throw 'take C:\peel-dev\gui.lock first: run the driver inside Invoke-GuiRun' }
    # Inside the lock, so a result being written by another run is never deleted.
    Remove-Item $ResultPath -ErrorAction SilentlyContinue
    $runDir = Join-Path $Dir ('runs\{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $runDir | Out-Null
    Add-GuiLockRun $runDir
    $drivingPath = Join-Path $runDir 'driving.txt'
    $pidPath = Join-Path $runDir 'driver.pid'
    $task = '{0}-{1}' -f $Name, ([guid]::NewGuid().ToString('N').Substring(0, 8))
    # $env:USERDOMAIN is WORKGROUP on George, which is not an account domain.
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel $RunLevel
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds ($IdleTimeout + $TimeoutSeconds + 300))
    # Hidden: a console window on Daniel's desktop gets closed (which kills
    # the driver mid drag) and can sit over the windows the driver aims at.
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-WindowStyle Hidden {0} -GuardDir "{1}"' -f $Arguments, $runDir)
    $timedOut = $false
    $vanished = $false
    $teardown = $null
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds($IdleTimeout + 120)
        $driving = $false
        $driver = 0
        $missing = 0
        do {
            Start-Sleep -Seconds 3
            if (-not $driving -and (Test-Path $drivingPath)) {
                $driving = $true
                $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
                if ($OnDriving) { & $OnDriving }
            }
            if (-not $driver -and (Test-Path $pidPath)) { $driver = [int](Get-Content $pidPath -Raw) }
            # Once the driver has told us its pid, its process is the truth:
            # a task can be deleted by someone else, and Get-ScheduledTask can
            # fail for a moment, while the driver is still driving.
            if ($driver) {
                $state = if (Get-Process -Id $driver -ErrorAction SilentlyContinue) { 'Running' } else { 'Ended' }
            }
            else {
                $found = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                $state = if ($found) { [string]$found.State } else { 'Missing' }
                if ($state -eq 'Missing' -and ++$missing -lt 3) { $state = 'Running' }
                if ($state -eq 'Queued') { $state = 'Running' }
            }
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ($state -eq 'Running') {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            if ($driver) { Stop-Process -Id $driver -Force -ErrorAction SilentlyContinue }
            $timedOut = $true
            Write-Host $(if ($driving) { "TIMEOUT after $TimeoutSeconds seconds of driving" } else { "TIMEOUT: the driver never started to drive within $IdleTimeout s" })
        }
        elseif ($state -eq 'Missing') {
            $vanished = $true
            Write-Host "the scheduled task $task disappeared before its driver started"
        }
        elseif (-not (Test-Path $ResultPath)) {
            $info = Get-ScheduledTaskInfo -TaskName $task -ErrorAction SilentlyContinue
            $last = if ($info) { '0x{0:x8}' -f [uint32]$info.LastTaskResult } else { 'unknown (the task is gone)' }
            Write-Host "the driver (pid $driver) ended with no result: task $task last result $last"
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
        $died = $timedOut -or $vanished -or -not (Test-Path $ResultPath)
        if ($died -and (Test-Path $pidPath)) {
            $driver = [int](Get-Content $pidPath -Raw)
            if (Get-Process -Id $driver -ErrorAction SilentlyContinue) {
                Stop-Process -Id $driver -Force -ErrorAction SilentlyContinue
                Write-Host "killed the driver, pid $driver"
            }
        }
        # Give a killed driver's watchdog time to report before the runner acts.
        if ($died -and (Test-Path $pidPath)) {
            $wait = (Get-Date).AddSeconds(15)
            while (-not (Test-Path (Join-Path $runDir 'teardown-watchdog.json')) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 500 }
        }
        $teardown = Complete-GuiRun -RunDir $runDir -DriverDied $died
    }
    return [pscustomobject]@{ TimedOut = $timedOut; Died = $died; Teardown = $teardown; RunDir = $runDir }
}

# Runner side, after the driver's task ended: print the watchdog's report if
# the watchdog had to act, and when the driver timed out or left no result,
# run the runner's teardown (it acts only on a state nobody finished), with a
# screenshot. Returns the runner's report.
function Complete-GuiRun {
    param([Parameter(Mandatory)] [string]$RunDir, [bool]$DriverDied)
    $watchdog = Join-Path $RunDir 'teardown-watchdog.json'
    if (Test-Path $watchdog) {
        $w = Get-Content $watchdog -Raw | ConvertFrom-Json
        Write-Host "TEARDOWN (watchdog): $($w.text)"
    }
    if (-not $DriverDied) { return $null }
    $r = Invoke-InteractiveTeardown -RunDir $RunDir -Report (Join-Path $RunDir 'teardown-runner.json') -Shot (Join-Path $RunDir 'shot-after-teardown.png')
    if ($r) { Write-Host "TEARDOWN (runner): $($r.text); screenshot $($r.shot)" }
    else { Write-Host 'TEARDOWN (runner): the interactive teardown did not report (is Daniel signed in?)' }
    return $r
}
