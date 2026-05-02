namespace FileTransfer;

/// <summary>Path-safety helpers to prevent path traversal attacks.</summary>
public static class SafePath
{
    /// <summary>Check that <paramref name="candidate"/> resolves to a path inside <paramref name="root"/>.</summary>
    public static bool IsUnderRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Windows reserved device names — these can't be used as filenames.</summary>
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Returns true when <paramref name="relativePath"/> contains dangerous sequences.</summary>
    public static bool IsDangerous(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // Reject extremely long paths (well beyond MAX_PATH = 260).
        if (relativePath.Length > 500) return true;

        // Block path traversal and UNC paths.
        // Allow backslash/forward-slash — they are legitimate subdirectory separators.
        // Check for ".." as a PATH COMPONENT (directory traversal), not just substring.
        // "filename..pdf" is legitimate — two dots in a filename is OK.
        // Only block: "..\", "../", starts with "..", ends with "\..", "/.."
        if (relativePath.Contains(@"..\") || relativePath.Contains("../")) return true;
        if (relativePath == ".." || relativePath.StartsWith(@"..\" ) || relativePath.StartsWith("../")) return true;
        if (relativePath.EndsWith(@"\..") || relativePath.EndsWith("/..")) return true;
        if (relativePath.StartsWith(@"\\") || relativePath.StartsWith("//")) return true;
        // Block absolute paths with drive letter (e.g. "C:\foo") but not "subfolder\file.txt".
        // Path.IsPathRooted returns true for "\foo" and "sub\file" on Windows, so check for
        // drive letter explicitly instead.
        if (relativePath.Length >= 2 && relativePath[1] == ':' && char.IsLetter(relativePath[0])) return true;
        // Also block Unix-style absolute paths.
        if (relativePath.StartsWith("/")) return true;

        // Block Windows reserved device names (COM1, NUL, CON, PRN, AUX, LPT1, etc.).
        // Check the base filename (without extension) of each path component.
        var parts = relativePath.Split('\\', '/');
        foreach (var part in parts)
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(part);
            if (WindowsReservedNames.Contains(nameWithoutExt)) return true;
        }

        return false;
    }

    /// <summary>Deny-list of critical Windows system directories (case-insensitive prefix match).</summary>
    private static readonly string[] DeniedPrefixes = new[]
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",
        @"C:\$Recycle.Bin",
    };

    /// <summary>Check that <paramref name="path"/> is not inside a protected system directory.</summary>
    public static bool IsSystemPath(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var prefix in DeniedPrefixes)
        {
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
