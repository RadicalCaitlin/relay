using System.Runtime.InteropServices;

namespace Relay.Core.Storage;

/// <summary>
/// Replaces a file in one step: the new contents are written whole beside it and renamed over it, so
/// an interrupted write leaves either the old file or the new one, both complete, never a truncated one.
/// </summary>
internal static class AtomicFile
{
    /// <param name="write">Writes the new contents. Anything that restricts who may read them comes first.</param>
    public static void Write(string target, Action<FileStream> write)
    {
        var staging = target + ".new";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            MoveOver(staging, target);
        }
        finally
        {
            File.Delete(staging);
        }

        if (!OperatingSystem.IsWindows())
            FlushDirectory(Path.GetDirectoryName(target)!);
    }

    private static void MoveOver(string staging, string target)
    {
        // Windows refuses the rename while either file is open: the target for as long as another Relay
        // takes to read it, the staging file for as long as a virus scanner or the search indexer looks
        // at what was just written. Both pass in a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staging, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    /// <summary>
    /// Makes the rename itself survive a power loss, not just the new file's contents. Best effort:
    /// the file is complete either way, and not every file system can flush a directory.
    /// </summary>
    private static void FlushDirectory(string directory)
    {
        var descriptor = open(directory, 0);
        if (descriptor < 0)
            return;

        _ = fsync(descriptor);
        _ = close(descriptor);
    }

    [DllImport("libc")]
    private static extern int open(string path, int flags);

    [DllImport("libc")]
    private static extern int fsync(int descriptor);

    [DllImport("libc")]
    private static extern int close(int descriptor);
}
