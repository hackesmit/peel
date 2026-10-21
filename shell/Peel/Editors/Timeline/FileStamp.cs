// Is this still the file the editor was opened on?
//
// The image editors hash the whole file (PreviewSource) because they decode
// it anyway. A video can be gigabytes, and hashing it twice would make the
// window slow to open and slow to apply, so the timeline and page editors
// compare what Windows says about the file instead: its size and its last
// write time. A cut list marked on one recording, or a page order chosen on
// one PDF, must never be applied to another that took its name.
using System;
using System.IO;

namespace Peel.Editors.Timeline;

internal sealed class FileStamp
{
    private readonly long _length;
    private readonly DateTime _written;

    private FileStamp(long length, DateTime written)
    {
        _length = length;
        _written = written;
    }

    internal static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception error)
        {
            Log.Write($"stamp: cannot read {path}", error);
            return null;
        }
    }

    internal bool StillMatches(string path, out string problem)
    {
        var now = Of(path);
        if (now is null)
        {
            problem = $"Peel cannot read {Path.GetFileName(path)} any more. Open it again.";
            return false;
        }
        if (now._length == _length && now._written == _written)
        {
            problem = string.Empty;
            return true;
        }
        problem = $"{Path.GetFileName(path)} changed after the editor opened, so nothing was written. Open it again.";
        return false;
    }
}
