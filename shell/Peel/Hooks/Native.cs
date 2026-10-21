// Every Win32 call the shell makes. Kept in one file so the unsafe surface is
// small and reviewable; nothing else in Peel declares a DllImport.
using System;
using System.Runtime.InteropServices;

namespace Peel.Hooks;

internal static class Native
{
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;

    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    internal const int SM_CXDRAG = 68;
    internal const int SM_CYDRAG = 69;

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const int RGN_DIFF = 4;

    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public int X;
        public int Y;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    internal const int SM_SWAPBUTTON = 23;

    /// <summary>
    /// The physical state of the primary button, asked of Windows rather than
    /// remembered from the hook. A low level hook that answers too slowly is
    /// skipped silently, and a button up over an elevated window or a secure
    /// desktop never reaches a medium integrity hook, so a button up can go
    /// missing; this cannot. GetAsyncKeyState reads physical buttons, so with
    /// the buttons swapped the primary one is VK_RBUTTON.
    /// </summary>
    internal static bool LeftButtonDown()
    {
        var key = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? 0x02 : 0x01;
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public RECT CaretRect;
    }

    internal const int GUI_INMOVESIZE = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);

    /// <summary>
    /// GUI state of a thread (0 means the foreground thread). None of these
    /// calls sends a message to the other process, so they cannot hang on a
    /// window that has stopped responding; they are safe on the hook thread.
    /// </summary>
    internal static bool ThreadInfo(uint threadId, out GUITHREADINFO info)
    {
        info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(threadId, ref info);
    }

    /// <summary>The thread that owns the top level window under a screen point, or 0.</summary>
    internal static uint ThreadAt(int x, int y)
    {
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        return hwnd == IntPtr.Zero ? 0 : GetWindowThreadProcessId(hwnd, out _);
    }

    internal static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }
        var name = new System.Text.StringBuilder(128);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public int Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Tick count (GetTickCount clock) of the last input in this session, or null.</summary>
    internal static uint? LastInputTick()
    {
        var info = new LASTINPUTINFO { Size = Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.Time : null;
    }

    // Job objects: every engine process lives in one, so quitting or killing
    // Peel takes the engine and anything it started down with it.
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    // Starting an engine process suspended, so it is in its job before it runs
    // a single instruction (Jobs/ContainedProcess.cs).
    internal const uint CREATE_SUSPENDED = 0x00000004;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_NO_WINDOW = 0x08000000;
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new(0x00020002);
    // Windows 10 1607 and later: the process is created already in these jobs.
    internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = new(0x0002000D);

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string applicationName,
        System.Text.StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    internal static long GetWindowStyleEx(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    internal static void SetWindowStyleEx(IntPtr hwnd, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(hwnd, GWL_EXSTYLE, (int)value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Scale factor (1.0 at 96 DPI) of the monitor under a screen point.</summary>
    internal static double ScaleForPoint(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0 /* effective */, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // Pre Windows 8.1. Fall through to the system default.
        }
        catch (EntryPointNotFoundException)
        {
        }
        return 1.0;
    }
}
