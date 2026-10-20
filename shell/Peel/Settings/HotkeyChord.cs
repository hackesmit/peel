// The keyboard path's hotkey as data: modifiers plus one key, written the way
// the settings file and the window show it ("Ctrl+Alt+Enter"). Pure, so the
// unit tests pin the round trip with no window and no RegisterHotKey.
using System;
using System.Collections.Generic;
using System.Linq;
using Peel.Keyboard;

namespace Peel.Settings;

internal readonly record struct HotkeyChord(bool Ctrl, bool Alt, bool Shift, bool Win, uint Vk)
{
    internal static readonly HotkeyChord Default = new(true, true, false, false, KeyboardNative.VK_RETURN);

    private static readonly IReadOnlyDictionary<uint, string> Names = BuildNames();

    /// <summary>The RegisterHotKey modifier flags, MOD_NOREPEAT included.</summary>
    internal uint ModifierFlags =>
        (Ctrl ? KeyboardNative.MOD_CONTROL : 0)
        | (Alt ? KeyboardNative.MOD_ALT : 0)
        | (Shift ? KeyboardNative.MOD_SHIFT : 0)
        | (Win ? KeyboardNative.MOD_WIN : 0)
        | KeyboardNative.MOD_NOREPEAT;

    /// <summary>
    /// A chord Peel accepts: a key it can name and at least one of Ctrl, Alt
    /// or Win, so a global hotkey never eats plain typing or Shift+letter.
    /// </summary>
    internal bool Usable => Names.ContainsKey(Vk) && (Ctrl || Alt || Win);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Names.TryGetValue(Vk, out var name) ? name : $"0x{Vk:X2}");
        return string.Join("+", parts);
    }

    /// <summary>"Ctrl+Alt+Enter" to a chord, or null when it is not a usable one.</summary>
    internal static HotkeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }
        bool ctrl = false, alt = false, shift = false, win = false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": ctrl = true; break;
                case "ALT": alt = true; break;
                case "SHIFT": shift = true; break;
                case "WIN" or "WINDOWS": win = true; break;
                default: return null;
            }
        }
        var key = parts[^1];
        var vk = Names.FirstOrDefault(pair => string.Equals(pair.Value, key, StringComparison.OrdinalIgnoreCase));
        if (vk.Value is null)
        {
            return null;
        }
        var chord = new HotkeyChord(ctrl, alt, shift, win, vk.Key);
        return chord.Usable ? chord : null;
    }

    /// <summary>The chord a key press in the settings window makes, or null for a bare modifier.</summary>
    internal static HotkeyChord? FromPress(bool ctrl, bool alt, bool shift, bool win, uint vk)
    {
        var chord = new HotkeyChord(ctrl, alt, shift, win, vk);
        return chord.Usable ? chord : null;
    }

    private static IReadOnlyDictionary<uint, string> BuildNames()
    {
        var names = new Dictionary<uint, string>
        {
            [0x0D] = "Enter",
            [0x20] = "Space",
            [0x09] = "Tab",
            [0x08] = "Backspace",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x24] = "Home",
            [0x23] = "End",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0xC0] = "`",
            [0xBA] = ";",
            [0xBC] = ",",
            [0xBE] = ".",
            [0xBF] = "/",
        };
        for (uint vk = 'A'; vk <= 'Z'; vk++)
        {
            names[vk] = ((char)vk).ToString();
        }
        for (uint vk = '0'; vk <= '9'; vk++)
        {
            names[vk] = ((char)vk).ToString();
        }
        for (uint n = 1; n <= 24; n++)
        {
            names[0x6F + n] = $"F{n}";
        }
        return names;
    }
}
