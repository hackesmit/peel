// Does this file carry an orientation the engine will apply?
//
// image.load opens every picture upright: pillow's exif_transpose reads the
// EXIF Orientation tag wherever pillow finds EXIF (a JPEG APP1, a PNG eXIf
// chunk or "Raw profile type exif" text, a WebP EXIF chunk, a TIFF's own first
// directory) and, when there is no EXIF Orientation, an XMP tiff:Orientation.
// WPF reads the tag for JPEG and nothing else, so a PNG or WebP stored on its
// side was shown on its side while the engine cropped or redacted it upright:
// a redaction then lands on the wrong part of the picture.
//
// This reads the bytes the preview is decoded from and answers one question:
// can WPF's own decode be trusted to be what the engine sees? Any doubt (a
// tag that is not 1, EXIF this cannot parse, a raw profile, an XMP
// orientation, plain or in a compressed text chunk) sends the file to the engine to rasterise, which is upright by
// construction. Being wrong in that direction costs a second; being wrong in
// the other costs a leak.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Peel.Editors;

internal static class OrientationProbe
{
    private static readonly Regex XmpOrientation = new(
        "tiff:Orientation(?:=\"|>)\\s*([0-9])", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the file may open turned in the engine: an EXIF or XMP
    /// orientation other than 1 in any container, or EXIF that cannot be read
    /// here. False only when every place pillow looks says upright or nothing.
    /// </summary>
    internal static bool MayBeTurned(ReadOnlySpan<byte> file)
    {
        try
        {
            var exif = ExifOrientation(file);
            if (exif == Unknown)
            {
                return true;
            }
            if (exif is not None and not 1)
            {
                return true;
            }
            // pillow only consults XMP when EXIF has no Orientation, but a
            // stray XMP orientation is cheap to honour either way.
            return exif == None && XmpTurned(file);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private const int None = 0;
    private const int Unknown = -1;

    /// <summary>The EXIF Orientation, None when there is no EXIF tag, Unknown when unreadable.</summary>
    private static int ExifOrientation(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 4 && ((file[0] == 'I' && file[1] == 'I' && file[2] == 42 && file[3] == 0)
            || (file[0] == 'M' && file[1] == 'M' && file[2] == 0 && file[3] == 42)))
        {
            return FromTiff(file);
        }
        if (file.Length >= 3 && file[0] == 0xFF && file[1] == 0xD8)
        {
            return FromJpeg(file);
        }
        if (file.Length >= 8 && file[..8].SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return FromPng(file);
        }
        if (file.Length >= 12 && Ascii(file, 0, 4) == "RIFF" && Ascii(file, 8, 4) == "WEBP")
        {
            return FromWebp(file);
        }
        // BMP, GIF and the rest carry no EXIF that pillow applies; formats WPF
        // cannot decode go to the engine anyway.
        return None;
    }

    private static int FromJpeg(ReadOnlySpan<byte> file)
    {
        var at = 2;
        var found = None;
        while (at + 4 <= file.Length)
        {
            if (file[at] != 0xFF)
            {
                return found;
            }
            var marker = file[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker == 0xD9 || marker == 0xDA)
            {
                return found;
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }
            var length = (file[at + 2] << 8) | file[at + 3];
            if (length < 2 || at + 2 + length > file.Length)
            {
                return found == None ? Unknown : found;
            }
            var body = file.Slice(at + 4, length - 2);
            if (marker == 0xE1 && body.Length >= 6 && Ascii(body, 0, 4) == "Exif" && body[4] == 0 && body[5] == 0)
            {
                // pillow uses the first EXIF segment.
                if (found == None)
                {
                    found = FromTiff(body[6..]);
                }
            }
            at += 2 + length;
        }
        return found;
    }

    private static int FromPng(ReadOnlySpan<byte> file)
    {
        var at = 8;
        var found = None;
        while (at + 12 <= file.Length)
        {
            var length = BigEndian32(file, at);
            if (length < 0 || at + 12L + length > file.Length)
            {
                return found == None ? Unknown : found;
            }
            var type = Ascii(file, at + 4, 4);
            var data = file.Slice(at + 8, length);
            if (type == "eXIf")
            {
                var tiff = data.Length >= 6 && Ascii(data, 0, 4) == "Exif" ? data[6..] : data;
                found = FromTiff(tiff);
            }
            else if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var keyword = Keyword(data);
                if (keyword.StartsWith("Raw profile type exif", StringComparison.OrdinalIgnoreCase)
                    || keyword.StartsWith("Raw profile type APP1", StringComparison.OrdinalIgnoreCase))
                {
                    // Hex encoded, maybe compressed: not worth decoding here.
                    return Unknown;
                }
                // An XMP packet in a compressed chunk is invisible to the
                // plain byte search in XmpTurned, but pillow inflates it and
                // turns the picture. Inflate it here too; failing that, doubt.
                if (CompressedTextTurned(type, data, keyword.Length))
                {
                    return Unknown;
                }
            }
            else if (type == "IEND")
            {
                break;
            }
            at += 12 + length;
        }
        return found;
    }

    // Pillow refuses a text chunk that inflates past this, so a bigger one
    // cannot turn the picture in the engine either; still, doubt costs only a
    // second, so it goes to the engine rather than being called upright.
    private const int MaxInflated = 1024 * 1024;

    /// <summary>
    /// True when a zTXt chunk or a compressed iTXt chunk holds an XMP
    /// orientation other than 1, or cannot be inflated. False for any other
    /// chunk, compressed or not (plain text is left to XmpTurned).
    /// </summary>
    private static bool CompressedTextTurned(string type, ReadOnlySpan<byte> data, int keywordLength)
    {
        int at;
        if (type == "zTXt")
        {
            // keyword NUL, method, zlib data
            at = keywordLength + 2;
        }
        else if (type == "iTXt")
        {
            // keyword NUL, flag, method, language NUL, translated keyword NUL, text
            var flag = keywordLength + 1;
            if (flag >= data.Length)
            {
                return true;
            }
            if (data[flag] == 0)
            {
                return false;
            }
            at = flag + 2;
            for (var nul = 0; nul < 2; nul++)
            {
                var end = at < data.Length ? data[at..].IndexOf((byte)0) : -1;
                if (end < 0)
                {
                    return true;
                }
                at += end + 1;
            }
        }
        else
        {
            return false;
        }
        if (at > data.Length)
        {
            return true;
        }
        try
        {
            using var input = new MemoryStream(data[at..].ToArray(), writable: false);
            using var inflate = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxInflated)
                {
                    return true;
                }
            }
            return XmpTurned(output.GetBuffer().AsSpan(0, (int)output.Length));
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static int FromWebp(ReadOnlySpan<byte> file)
    {
        var at = 12;
        while (at + 8 <= file.Length)
        {
            var type = Ascii(file, at, 4);
            var length = LittleEndian32(file, at + 4);
            if (length < 0 || at + 8L + length > file.Length)
            {
                return Unknown;
            }
            if (type == "EXIF")
            {
                var data = file.Slice(at + 8, length);
                var tiff = data.Length >= 6 && Ascii(data, 0, 4) == "Exif" ? data[6..] : data;
                return FromTiff(tiff);
            }
            at += 8 + length + (length & 1);
        }
        return None;
    }

    /// <summary>Tag 0x0112 of the first directory of a TIFF structure.</summary>
    private static int FromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return Unknown;
        }
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            little = false;
        }
        else
        {
            return Unknown;
        }
        var ifd = Read32(tiff, 4, little);
        if (ifd < 8 || ifd + 2L > tiff.Length)
        {
            return Unknown;
        }
        var count = Read16(tiff, ifd, little);
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + (i * 12);
            if (entry + 12L > tiff.Length)
            {
                return Unknown;
            }
            if (Read16(tiff, entry, little) != 0x0112)
            {
                continue;
            }
            var type = Read16(tiff, entry + 2, little);
            return type switch
            {
                3 => Read16(tiff, entry + 8, little),
                4 => Read32(tiff, entry + 8, little),
                _ => Unknown,
            };
        }
        return None;
    }

    private static bool XmpTurned(ReadOnlySpan<byte> file)
    {
        // A span search first: a 50 MB photo must not become a 100 MB string
        // just to learn it has no XMP orientation.
        var needle = "tiff:Orientation"u8;
        var rest = file;
        while (true)
        {
            var at = rest.IndexOf(needle);
            if (at < 0)
            {
                return false;
            }
            var window = rest.Slice(at, Math.Min(48, rest.Length - at));
            var match = XmpOrientation.Match(Encoding.Latin1.GetString(window));
            if (match.Success && match.Groups[1].Value != "1")
            {
                return true;
            }
            rest = rest[(at + needle.Length)..];
        }
    }

    private static string Keyword(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? data : data[..end]);
    }

    private static string Ascii(ReadOnlySpan<byte> data, int at, int length) =>
        Encoding.ASCII.GetString(data.Slice(at, length));

    private static int BigEndian32(ReadOnlySpan<byte> data, int at) =>
        (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

    private static int LittleEndian32(ReadOnlySpan<byte> data, int at) =>
        data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);

    private static int Read16(ReadOnlySpan<byte> data, int at, bool little) => little
        ? data[at] | (data[at + 1] << 8)
        : (data[at] << 8) | data[at + 1];

    private static int Read32(ReadOnlySpan<byte> data, int at, bool little) => little
        ? LittleEndian32(data, at)
        : BigEndian32(data, at);
}
