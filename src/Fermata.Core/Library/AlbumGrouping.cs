using System.Text;
using Fermata.Text;

namespace Fermata.Library;

/// <summary>Decides which tracks form an album.</summary>
/// <remarks>
/// Tracks form one album when they share an album title and an album folder, where a disc folder such as
/// "CD2" counts as its parent. A MusicBrainz release ID refines the grouping.
/// </remarks>
public static class AlbumGrouping
{
    public static string? Key(string path, string albumTitle, string? musicBrainzAlbumId)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return null;
        var key = new StringBuilder(128);
        TextFolding.AppendFolded(key, albumTitle);
        key.Append('\u001F').Append(AlbumDirectory(path));
        if (!string.IsNullOrEmpty(musicBrainzAlbumId))
            key.Append('\u001F').Append(musicBrainzAlbumId);
        return key.ToString();
    }

    /// <summary>The folder that holds a file's album, which is the file's folder or, for a disc folder, its parent.</summary>
    public static string AlbumDirectory(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? "";
        return IsDiscFolder(Path.GetFileName(directory.AsSpan())) ? Path.GetDirectoryName(directory) ?? directory : directory;
    }

    /// <summary>Matches "CD1", "Disc 2", "disk_03", "CD 1 - Bonus" and similar names.</summary>
    public static bool IsDiscFolder(ReadOnlySpan<char> name)
    {
        name = name.Trim();
        int prefix = name.StartsWith("cd", StringComparison.OrdinalIgnoreCase) ? 2
            : name.StartsWith("disc", StringComparison.OrdinalIgnoreCase) || name.StartsWith("disk", StringComparison.OrdinalIgnoreCase) ? 4
            : 0;
        if (prefix == 0)
            return false;
        var rest = name[prefix..].TrimStart(" _-.");
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;
        return digits is 1 or 2 && (digits == rest.Length || !char.IsLetterOrDigit(rest[digits]));
    }
}
