using System;
using System.IO;
using System.Text;

namespace Koda.Playback;

/// <summary>
/// A diagnostic trace of video starts and seeks: what the picture's player, the sound's player
/// and their shared clock each report, to <c>video-trace.txt</c> in the data folder. Lines are
/// written on the UI thread as they happen; the file restarts once it passes 512 KB.
/// </summary>
internal static class VideoTrace
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                _path ??= Path.Combine(App.DataDirectory, "video-trace.txt");
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n", Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // A trace that can't be written changes nothing about playback.
        }
    }
}
