// The three Win32 calls the keyboard subsystem needs that Hooks/Native.cs does
// not already declare.
//
// Hooks/Native.cs keeps the convention that the shell's unsafe surface lives in
// one file. It stays the home for every P/Invoke the drag path uses, and the
// keyboard path reuses it for everything it can (GetCursorPos, SetWindowPos,
// ScaleForPoint, CreateEllipticRgn, SetWindowRgn, the WS_EX_* styles and so on).
// These three are the only calls it lacks. They live here, not in Native.cs,
// because this bead owns shell/Peel/Keyboard/* and must not edit Hooks/Native.cs,
// which the tray-shell bead and the parallel action/editor beads own: a worker
// never shares a file with another. Keeping them in one file inside the owned
// folder preserves the spirit of the convention (a small, reviewable unsafe
// surface) without reaching across the ownership line.
using System;
using System.Runtime.InteropServices;

namespace Peel.Keyboard;

internal static class KeyboardNative
{
    internal const int WM_HOTKEY = 0x0312;

    // RegisterHotKey modifier flags.
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    /// <summary>Do not fire again while the chord is held down.</summary>
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const uint VK_RETURN = 0x0D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
}
