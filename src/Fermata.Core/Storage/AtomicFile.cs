namespace Fermata.Storage;

/// <summary>
/// Writes files so that readers (and a crash) see either the old or the new contents, never a
/// partial file: data goes to a sibling temporary file, is flushed to disk, then renamed over the target.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        Write(path, stream => stream.Write(copy));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
