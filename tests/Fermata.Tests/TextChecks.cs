using Fermata.Metadata;
using Fermata.Text;

namespace Fermata.Tests;

internal static class TextChecks
{
    public static void Run(Checks check)
    {
        (string Input, string Folded)[] folds =
        [
            ("Björk", "bjork"),
            ("Sigur Rós", "sigur ros"),
            ("Mötley Crüe", "motley crue"),
            ("AC/DC", "ac dc"),
            ("R.E.M.", "rem"),
            ("Guns N' Roses", "guns n roses"),
            ("Don’t Stop", "dont stop"),
            ("Straße", "strasse"),
            ("Æther Œuvre", "aether oeuvre"),
            ("  Hello,   World!  ", "hello world"),
            ("Łódź", "lodz"),
            ("Ǆemal", "dzemal"),
            ("Café del Mar", "cafe del mar"),
            ("été", "ete"),
            ("Щедрик Ёлка", "щедрик елка"),
            ("Ελλάδα", "ελλαδα"),
            ("Tiếng Việt", "tieng viet"),
            ("坂本龍一", "坂本龍一"),
            ("", ""),
            ("—", ""),
        ];
        foreach (var (input, folded) in folds)
            check.Equal(folded, TextFolding.Fold(input), $"Fold({input})");

        check.Equal("beatles", TextFolding.SortKey("The Beatles"), "sort key drops a leading article");
        check.Equal("theatre of tragedy", TextFolding.SortKey("Theatre of Tragedy"), "sort key keeps words that merely start with 'the'");
        check.Equal("the", TextFolding.SortKey("The"), "sort key keeps a name that is only an article");

        CheckSplit(check, "Daft Punk feat. Pharrell Williams", "Daft Punk", "Pharrell Williams");
        CheckSplit(check, "Simon & Garfunkel", "Simon & Garfunkel");
        CheckSplit(check, "Earth, Wind & Fire", "Earth, Wind & Fire");
        CheckSplit(check, "A; B; C", "A", "B", "C");
        CheckSplit(check, "Kenji Morrow Ft. Aurora Vale", "Kenji Morrow", "Aurora Vale");
        check.Equal("Artist A & Artist B", ArtistCredit.Display("Artist A; Artist B"), "a tag list of two reads as a pair");
        check.Equal("A, B & C", ArtistCredit.Display("A; B; C"), "a longer tag list reads as a list");
        check.Equal("A & B", ArtistCredit.Display("A / B"), "slash-separated credits read as a list");
        check.Equal("Daft Punk feat. Pharrell Williams", ArtistCredit.Display("Daft Punk feat. Pharrell Williams"), "credits as people write them are kept");
        check.Equal("Earth, Wind & Fire", ArtistCredit.Display("Earth, Wind & Fire"), "names with commas are kept");
        check.Equal("AC/DC", ArtistCredit.Display("AC/DC"), "a slash inside a name is kept");
        CheckSplit(check, "X vs. Y featuring Z", "X", "Y", "Z");
        CheckSplit(check, "Solo feat. Solo", "Solo");

        CheckGenres(check, "(17)", "Rock");
        CheckGenres(check, "17", "Rock");
        CheckGenres(check, "(17)(31)", "Rock", "Trance");
        CheckGenres(check, "(17)Hard Rock", "Hard Rock");
        CheckGenres(check, "Synthwave", "Synthwave");
        CheckGenres(check, "((Very) Heavy", "(Very) Heavy");
        CheckGenres(check, "Rock; Pop", "Rock", "Pop");
        CheckGenres(check, "(RX)", "Remix");
        CheckGenres(check, "(255)");

        check.Equal(2004, TextDecoding.Year("2004-05-12"), "year from ISO date");
        check.Equal(2004, TextDecoding.Year("12.05.2004"), "year from European date");
        check.Equal(1999, TextDecoding.Year(" 1999 "), "year with spaces");
        check.Equal(0, TextDecoding.Year("20041"), "five digits are not a year");
        check.Equal(0, TextDecoding.Year(""), "empty year");
        check.Equal((3, 12), TextDecoding.NumberPair("3/12"), "track pair");
        check.Equal((7, 0), TextDecoding.NumberPair("07"), "track without total");
        check.Equal((0, 0), TextDecoding.NumberPair("x"), "non-numeric track");
        check.That(TextDecoding.Flag("1") && TextDecoding.Flag("TRUE") && !TextDecoding.Flag("0"), "tag booleans");
    }

    private static void CheckSplit(Checks check, string credit, params string[] expected)
    {
        var names = ArtistCredit.Split(credit);
        check.That(names.SequenceEqual(expected), $"Split({credit}) = [{string.Join(" | ", names)}]");
    }

    private static void CheckGenres(Checks check, string value, params string[] expected)
    {
        var genres = Genres.ParseId3(value);
        check.That(genres.SequenceEqual(expected), $"ParseId3({value}) = [{string.Join(" | ", genres)}]");
    }
}
