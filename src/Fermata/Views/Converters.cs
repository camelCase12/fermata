using Avalonia.Data.Converters;
using Fermata.Library;

namespace Fermata.Views;

public static class Converters
{
    /// <summary>True for a positive count (show a section only when it has items).</summary>
    public static readonly IValueConverter Positive = new FuncValueConverter<int, bool>(count => count > 0);

    /// <summary>One cover as a list of covers (for an ambient backdrop), or none.</summary>
    public static readonly IValueConverter OneCover = new FuncValueConverter<ArtSource?, IReadOnlyList<ArtSource>>(art =>
        art is null ? [] : [art]);

    /// <summary>The cover for a track (embedded, album or folder art).</summary>
    public static readonly IValueConverter TrackArt = new FuncValueConverter<Track?, ArtSource?>(track =>
        track is not null && App.Shell is { } shell ? shell.Library.ArtOf(track) : null);

    /// <summary>The number of columns of tiles at least 200 pixels wide that fit in a width.</summary>
    public static readonly IValueConverter TileColumns = new FuncValueConverter<double, int>(width =>
        Math.Clamp((int)(width / 200), 1, 6));

    /// <summary>The number of columns of quick picks at least 300 pixels wide that fit in a width, chosen to divide twelve picks evenly.</summary>
    public static readonly IValueConverter PickColumns = new FuncValueConverter<double, int>(width =>
        ((int[])[6, 4, 3, 2]).FirstOrDefault(columns => width / columns >= 300, 1));

    /// <summary>A playlist's length, such as "14 songs".</summary>
    public static readonly IValueConverter SongCount = new FuncValueConverter<Playlist?, string>(playlist =>
        playlist is null ? "" : ViewModels.Formats.Count(playlist.Paths.Count, "song"));

}
