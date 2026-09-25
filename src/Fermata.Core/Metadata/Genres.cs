namespace Fermata.Metadata;

internal static class Genres
{
    /// <summary>ID3v1 genres including the Winamp extensions (indices 0–191).</summary>
    private static readonly string[] Id3v1 =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal",
        "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock", "Techno", "Industrial",
        "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk",
        "Fusion", "Trance", "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
        "Alternative Rock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic",
        "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta",
        "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American", "Cabaret", "New Wave", "Psychedelic", "Rave", "Showtunes",
        "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
        "Folk", "Folk-Rock", "National Folk", "Swing", "Fast Fusion", "Bebop", "Latin", "Revival", "Celtic", "Bluegrass",
        "Avantgarde", "Gothic Rock", "Progressive Rock", "Psychedelic Rock", "Symphonic Rock", "Slow Rock", "Big Band", "Chorus", "Easy Listening", "Acoustic",
        "Humour", "Speech", "Chanson", "Opera", "Chamber Music", "Sonata", "Symphony", "Booty Bass", "Primus", "Porn Groove",
        "Satire", "Slow Jam", "Club", "Tango", "Samba", "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle",
        "Duet", "Punk Rock", "Drum Solo", "A Cappella", "Euro-House", "Dance Hall", "Goa", "Drum & Bass", "Club-House", "Hardcore Techno",
        "Terror", "Indie", "BritPop", "Negerpunk", "Polsk Punk", "Beat", "Christian Gangsta Rap", "Heavy Metal", "Black Metal", "Crossover",
        "Contemporary Christian", "Christian Rock", "Merengue", "Salsa", "Thrash Metal", "Anime", "Jpop", "Synthpop", "Abstract", "Art Rock",
        "Baroque", "Bhangra", "Big Beat", "Breakbeat", "Chillout", "Downtempo", "Dub", "EBM", "Eclectic", "Electro",
        "Electroclash", "Emo", "Experimental", "Garage", "Global", "IDM", "Illbient", "Industro-Goth", "Jam Band", "Krautrock",
        "Leftfield", "Lounge", "Math Rock", "New Romantic", "Nu-Breakz", "Post-Punk", "Post-Rock", "Psytrance", "Shoegaze", "Space Rock",
        "Trop Rock", "World Music", "Neoclassical", "Audiobook", "Audio Theatre", "Neue Deutsche Welle", "Podcast", "Indie Rock", "G-Funk", "Dubstep",
        "Garage Rock", "Psybient",
    ];

    public static string? FromIndex(int index) => (uint)index < (uint)Id3v1.Length ? Id3v1[index] : null;

    /// <summary>
    /// Expands an ID3v2 TCON value: "(17)", "(17)Rock", "17", "(RX)", "(CR)" or plain text.
    /// </summary>
    public static List<string> ParseId3(string value)
    {
        var results = new List<string>(1);
        var text = value.AsSpan().Trim();
        if (text.IsEmpty)
            return results;
        if (int.TryParse(text, out int bare))
        {
            if (FromIndex(bare) is { } name)
                results.Add(name);
            return results;
        }
        while (text.Length > 0 && text[0] == '(' && !text.StartsWith("(("))
        {
            int close = text.IndexOf(')');
            if (close < 0)
                break;
            var reference = text[1..close];
            if (int.TryParse(reference, out int index) && FromIndex(index) is { } named)
                results.Add(named);
            else if (reference.SequenceEqual("RX"))
                results.Add("Remix");
            else if (reference.SequenceEqual("CR"))
                results.Add("Cover");
            text = text[(close + 1)..];
        }
        // Text after the references refines them ("(17)Hard Rock"); "((" escapes a literal parenthesis.
        string refinement = text.ToString().Replace("((", "(").Trim();
        if (refinement.Length > 0)
        {
            results.Clear();
            results.AddRange(Split(refinement));
        }
        return results;
    }

    /// <summary>Splits multiple genres written into one value with semicolons or NULs.</summary>
    public static IEnumerable<string> Split(string value)
    {
        foreach (var part in value.Split([';', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }
}
