// Seconds as the user reads them, and as the engine parses them.
//
// The engine's timecode grammar (registry._parse_timecode) is plain seconds or
// up to three colon separated parts, "1:02:03.5". The window shows the same
// form and parses the same form, so a time typed here is never refused there.
// Every number crosses with InvariantCulture: under a comma locale "3,5" would
// otherwise reach the engine, which splits a list on commas.
using System;
using System.Globalization;

namespace Peel.Editors.Timeline;

internal static class Timecode
{
    /// <summary>
    /// "0:03.250", "1:02:03.500": minutes always, hours only when there are
    /// any, milliseconds always, so a column of times lines up.
    /// </summary>
    internal static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }
        var millis = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        var hours = millis / 3_600_000;
        var minutes = millis / 60_000 % 60;
        var rest = millis % 60_000 / 1000.0;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00.000}", hours, minutes, rest)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", minutes, rest);
    }

    /// <summary>
    /// What is sent to the engine: plain seconds, at most three decimals, no
    /// separators. "3.25", never "3,25" and never "0:03.250", because a range
    /// is written "start-end" and a list is split on commas.
    /// </summary>
    internal static string Engine(double seconds) =>
        Math.Round(Math.Max(0, seconds), 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parse what a user typed. Accepts what the engine accepts and nothing
    /// that it would read differently: no sign, no exponent, no thousands
    /// separator, at most three parts, and every part after the first below 60.
    /// </summary>
    internal static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }
        var parts = trimmed.Split(':');
        if (parts.Length > 3)
        {
            return false;
        }
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0 || !IsPlainNumber(part)
                || !double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }
            if (i > 0 && value >= 60)
            {
                return false;
            }
            total = total * 60 + value;
        }
        if (!double.IsFinite(total))
        {
            return false;
        }
        seconds = total;
        return true;
    }

    private static bool IsPlainNumber(string part)
    {
        var dots = 0;
        foreach (var c in part)
        {
            if (c == '.')
            {
                dots++;
            }
            else if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return dots <= 1 && part != ".";
    }
}
