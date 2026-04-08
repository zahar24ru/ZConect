using System.IO;

namespace UiApp.Models;

public sealed class FileItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string SizeText => IsDirectory ? "<DIR>" : FormatSize(Size);

    /// <summary>SVG Path geometry for file type icon.</summary>
    public string IconGeometry => IsDirectory ? FolderIcon : GetFileIcon(Path.GetExtension(Name).ToLowerInvariant());

    /// <summary>Icon fill color by type.</summary>
    public string IconColor => IsDirectory ? "#F0A030" : GetIconColor(Path.GetExtension(Name).ToLowerInvariant());

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024 * 1024)} MB";
        return $"{bytes / (1024L * 1024 * 1024)} GB";
    }

    // --- Icons (16x16 viewbox) ---

    private const string FolderIcon =
        "M1 2 C0.4 2 0 2.4 0 3 V13 C0 13.6 0.4 14 1 14 H15 C15.6 14 16 13.6 16 13 V5 C16 4.4 15.6 4 15 4 H8 L6.5 2 Z";

    private const string FileGeneric =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z";

    private const string FileImage =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z M5 8 A1.5 1.5 0 1 0 5 11 A1.5 1.5 0 1 0 5 8 Z M4 14 L6.5 11 L8 12.5 L10.5 9 L12 14 Z";

    private const string FileCode =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z M5.5 8 L3.5 10.5 L5.5 13 L4.5 13.5 L2 10.5 L4.5 7.5 Z M10.5 8 L12.5 10.5 L10.5 13 L11.5 13.5 L14 10.5 L11.5 7.5 Z M7 14 L9 7 L10 7.3 L8 14.3 Z";

    private const string FileArchive =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z M6 7 H8 V8 H6 Z M7 8.5 H9 V9.5 H7 Z M6 10 H8 V11 H6 Z M7 11.5 H9 V12.5 H7 Z";

    private const string FileAudio =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z M10 8 V12.5 A1.5 1.5 0 1 1 8 11 V9.5 H10 Z";

    private const string FileVideo =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z M5.5 8 L11 11 L5.5 14 Z";

    private const string FileExe =
        "M3 0 C2.4 0 2 0.4 2 1 V15 C2 15.6 2.4 16 3 16 H13 C13.6 16 14 15.6 14 15 V5 L9 0 Z M9 1 V4 C9 4.6 9.4 5 10 5 H13 Z M8 7 A3.5 3.5 0 1 0 8 14 A3.5 3.5 0 1 0 8 7 Z M7 9 H9 V10.5 L10 12 H6 L7 10.5 Z";

    private const string FileDisk =
        "M2 3 C1 3 0 4 0 5 V12 C0 13 1 14 2 14 H14 C15 14 16 13 16 12 V5 C16 4 15 3 14 3 Z M2 5 H14 V10 H2 Z M3 11 H5 V12.5 H3 Z M6 11 H8 V12.5 H6 Z";

    private static string GetFileIcon(string ext) => ext switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" or ".tiff" => FileImage,
        ".cs" or ".go" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".html" or ".css"
            or ".xml" or ".json" or ".yaml" or ".yml" or ".toml" or ".md" or ".txt" or ".log" or ".ini"
            or ".cfg" or ".conf" or ".sh" or ".bat" or ".ps1" or ".xaml" or ".csproj" or ".sln" => FileCode,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".cab" => FileArchive,
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".aac" or ".wma" or ".m4a" => FileAudio,
        ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => FileVideo,
        ".exe" or ".msi" or ".dll" or ".sys" => FileExe,
        ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" => FileDisk,
        _ => FileGeneric,
    };

    private static string GetIconColor(string ext) => ext switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" or ".tiff" => "#4CAF50",
        ".cs" or ".go" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".html" or ".css"
            or ".xml" or ".json" or ".yaml" or ".yml" or ".toml" or ".md" or ".txt" or ".log" or ".ini"
            or ".cfg" or ".conf" or ".sh" or ".bat" or ".ps1" or ".xaml" or ".csproj" or ".sln" => "#42A5F5",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".cab" => "#AB47BC",
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".aac" or ".wma" or ".m4a" => "#FF7043",
        ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => "#EF5350",
        ".exe" or ".msi" or ".dll" or ".sys" => "#78909C",
        ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" => "#8D6E63",
        _ => "#90A4AE",
    };
}
