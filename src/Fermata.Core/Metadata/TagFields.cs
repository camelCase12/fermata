namespace Fermata.Metadata;

/// <summary>
/// Maps textual field names shared by Vorbis comments, APEv2, Matroska, ASF and freeform
/// MP4/ID3 fields (TXXX, <c>----</c>) onto <see cref="AudioTags"/>.
/// </summary>
internal static class TagFields
{
    /// <summary>Applies one name/value pair. Unknown names are ignored; names compare case-insensitively.</summary>
    public static void Apply(AudioTags tags, string name, string value, TagReadOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        switch (Normalize(name))
        {
            case "TITLE":
                tags.SetTitle(value);
                break;
            case "ARTIST":
                // Several ARTIST fields list individual performers; together they form the credit.
                if (tags.ArtistValues.Count > 0 && tags.Artist is not null)
                    tags.Artist = tags.Artist + ", " + value.Trim();
                else
                    tags.SetArtist(value);
                tags.ArtistValues.Add(value.Trim());
                break;
            case "ARTISTS":
                tags.AddArtistName(value);
                break;
            case "ALBUMARTIST":
            case "ALBUM ARTIST":
            case "ALBUM_ARTIST":
            case "WM/ALBUMARTIST":
                tags.SetAlbumArtist(value);
                break;
            case "ALBUM":
            case "WM/ALBUMTITLE":
                tags.SetAlbum(value);
                break;
            case "DATE":
            case "YEAR":
            case "WM/YEAR":
            case "DATE_RELEASED":
            case "RELEASEDATE":
                tags.SetYear(TextDecoding.Year(value));
                break;
            case "ORIGINALDATE":
            case "ORIGINALYEAR":
            case "ORIGINAL YEAR":
            case "ORIGINALRELEASEDATE":
            case "WM/ORIGINALRELEASEYEAR":
            case "WM/ORIGINALRELEASETIME":
                tags.SetOriginalYear(TextDecoding.Year(value));
                break;
            case "TRACKNUMBER":
            case "TRACK":
            case "WM/TRACKNUMBER":
            case "PART_NUMBER":
            {
                var (number, total) = TextDecoding.NumberPair(value);
                tags.SetTrack(number, total);
                break;
            }
            case "TRACKTOTAL":
            case "TOTALTRACKS":
            case "TOTAL_PARTS":
                tags.SetTrack(0, TextDecoding.LeadingInt(value));
                break;
            case "DISCNUMBER":
            case "DISC":
            case "WM/PARTOFSET":
            {
                var (number, total) = TextDecoding.NumberPair(value);
                tags.SetDisc(number, total);
                break;
            }
            case "DISCTOTAL":
            case "TOTALDISCS":
                tags.SetDisc(0, TextDecoding.LeadingInt(value));
                break;
            case "GENRE":
            case "WM/GENRE":
                foreach (string genre in Genres.Split(value))
                    tags.AddGenre(genre);
                break;
            case "COMPOSER":
            case "WM/COMPOSER":
                tags.SetComposer(value);
                break;
            case "COMPILATION":
            case "WM/ISCOMPILATION":
                tags.Compilation |= TextDecoding.Flag(value);
                break;
            case "TITLESORT":
            case "WM/TITLESORTORDER":
                tags.TitleSort = AudioTags.KeepFirst(tags.TitleSort, value);
                break;
            case "ARTISTSORT":
            case "WM/ARTISTSORTORDER":
                tags.ArtistSort = AudioTags.KeepFirst(tags.ArtistSort, value);
                break;
            case "ALBUMSORT":
            case "WM/ALBUMSORTORDER":
                tags.AlbumSort = AudioTags.KeepFirst(tags.AlbumSort, value);
                break;
            case "ALBUMARTISTSORT":
            case "WM/ALBUMARTISTSORTORDER":
                tags.AlbumArtistSort = AudioTags.KeepFirst(tags.AlbumArtistSort, value);
                break;
            case "MUSICBRAINZ_ALBUMID":
            case "MUSICBRAINZ ALBUM ID":
            case "MUSICBRAINZ/ALBUM ID":
                tags.MusicBrainzAlbumId = AudioTags.KeepFirst(tags.MusicBrainzAlbumId, value);
                break;
            case "LYRICS":
            case "UNSYNCEDLYRICS":
            case "UNSYNCED LYRICS":
            case "WM/LYRICS":
                tags.HasLyrics = true;
                if (options.HasFlag(TagReadOptions.Lyrics))
                    tags.Lyrics = AudioTags.KeepFirst(tags.Lyrics, value);
                break;
        }
    }

    /// <summary>Whether a field of this name should be read in full even when it is large.</summary>
    public static bool IsLargeFieldWanted(string name, TagReadOptions options) => Normalize(name) switch
    {
        "LYRICS" or "UNSYNCEDLYRICS" or "UNSYNCED LYRICS" or "WM/LYRICS" => options.HasFlag(TagReadOptions.Lyrics),
        _ => false,
    };

    private static string Normalize(string name) => name.Trim().ToUpperInvariant();
}

[Flags]
public enum TagReadOptions
{
    None = 0,
    /// <summary>Read lyrics text (otherwise only <see cref="AudioTags.HasLyrics"/> is set).</summary>
    Lyrics = 1,
    /// <summary>Decode embedded picture bytes into <see cref="EmbeddedPicture.Data"/>.</summary>
    PictureData = 2,
}
