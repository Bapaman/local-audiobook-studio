using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Core.Storage;

public static class ProjectPaths
{
    public static string CanonicalRoot(string root)
    {
        try
        {
            string full = Path.GetFullPath(root);
            RejectReparse(full);
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (ProjectException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { throw new ProjectException("project.path"); }
    }

    public static string Resolve(string root, string relative)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new ProjectException("project.path");
            var parts = relative.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (part is ".." or "." or "" || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
                    throw new ProjectException("project.path");
            }
            string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ProjectException("project.path");
            RejectReparse(full);
            return full;
        }
        catch (ProjectException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { throw new ProjectException("project.path"); }
    }

    private static void RejectReparse(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectException("project.path");
    }
}
