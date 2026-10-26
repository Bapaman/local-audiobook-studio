using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

public static class WorkerPaths
{
    public static string Root(string root)
    {
        try { string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); RejectReparse(full); return full; }
        catch (WorkerException) { throw; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { throw new WorkerException("worker.path"); }
    }
    public static string Resolve(string root, string relative)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0')) throw new WorkerException("worker.path");
            string[] parts = relative.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
                    throw new WorkerException("worker.path");
            }
            string canonical = Root(root), full = Path.GetFullPath(Path.Combine(canonical, Path.Combine(parts)));
            string containmentPrefix = Path.EndsInDirectorySeparator(canonical) ? canonical : canonical + Path.DirectorySeparatorChar;
            if (!full.StartsWith(containmentPrefix, StringComparison.OrdinalIgnoreCase)) throw new WorkerException("worker.path");
            RejectReparse(full); return full;
        }
        catch (WorkerException) { throw; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { throw new WorkerException("worker.path"); }
    }
    private static void RejectReparse(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new WorkerException("worker.path"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
